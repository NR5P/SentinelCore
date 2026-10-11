window.GateWatchSettings = (function () {
  // The section/field definitions used to live right here as a 300-line SECTIONS array. They
  // now come from GET /api/settings/schema, where the iOS/macOS and Android apps read the very
  // same definitions - one source for every platform's settings forms (see SettingsSchema.cs).
  // Everything below only knows how to *render* a section, not what any section contains.

  // Schema field names can be dotted paths into nested settings objects ("apple.teamId" inside
  // PUSH NOTIFICATIONS) - these two are the only places that need to know that.
  function readPath(data, path) {
    let value = data;
    for (const segment of path.split(".")) {
      if (value == null) return undefined;
      value = value[segment];
    }
    return value;
  }

  function writePath(target, path, value) {
    const segments = path.split(".");
    let node = target;
    for (let i = 0; i < segments.length - 1; i++) {
      node = node[segments[i]] ??= {};
    }
    node[segments[segments.length - 1]] = value;
  }


  const WIDE_TYPES = new Set(["checkbox", "password", "csv"]);

  // A text field that can look itself up (schema field.lookup): the box gets a button beside it,
  // and what comes back fills in *other* fields of the same section - LOCATION's Town or City
  // setting latitude and longitude is the one that exists today. Nothing here knows that; the
  // candidate says which field names it sets, and this writes to whichever of them are on screen.
  function buildLookup(field, input) {
    const wrap = document.createElement("div");
    wrap.className = "field-lookup";

    const row = document.createElement("div");
    row.className = "field-lookup-row";
    const findBtn = document.createElement("button");
    findBtn.type = "button";
    findBtn.className = "field-toggle-btn";
    findBtn.textContent = field.lookup.label;
    findBtn.dataset.tip = field.lookup.tip;
    findBtn.setAttribute("data-tip-right", "");
    row.append(input, findBtn);

    const results = document.createElement("div");
    results.className = "field-lookup-results";
    wrap.append(row, results);

    function say(message, isError) {
      results.textContent = "";
      const note = document.createElement("p");
      note.className = "field-lookup-note" + (isError ? " field-lookup-error" : "");
      note.textContent = message;
      results.appendChild(note);
    }

    // Writing into siblings rather than into this field: a lookup exists precisely because the
    // values you want are not the thing you can type. Anything the candidate names that isn't
    // rendered here is skipped, so an older dashboard meeting a newer server writes what it can.
    function apply(candidate) {
      const grid = wrap.closest(".field-grid");
      for (const [name, value] of Object.entries(candidate.values || {})) {
        const target = grid && grid.querySelector(`[data-field="${name}"]`);
        if (!target) continue;
        target.value = value;
        target.dispatchEvent(new Event("change", { bubbles: true }));
      }
      say(`Filled in from ${candidate.label}. Press save to keep it.`, false);
    }

    async function find() {
      const query = input.value.trim();
      if (!query) {
        say("Type a town or city first.", true);
        input.focus();
        return;
      }

      findBtn.disabled = true;
      say("Looking…", false);
      try {
        const response = await fetch(`${field.lookup.endpoint}?query=${encodeURIComponent(query)}`);
        const payload = await response.json().catch(() => null);
        if (!response.ok) {
          // The server's own message is the useful one here - it distinguishes "this machine has
          // no route to the internet" from "the lookup service said no", and says what to do.
          say((payload && payload.error) || `Lookup failed (HTTP ${response.status}).`, true);
          return;
        }
        const candidates = (payload && payload.results) || [];
        if (candidates.length === 0) {
          say(`Nothing found for "${query}". Try adding a state or country, or enter the coordinates yourself.`, true);
          return;
        }

        results.textContent = "";
        const list = document.createElement("ul");
        list.className = "field-lookup-list";
        for (const candidate of candidates) {
          const item = document.createElement("li");
          const choice = document.createElement("button");
          choice.type = "button";
          choice.className = "field-lookup-choice";
          choice.textContent = candidate.label;
          choice.addEventListener("click", () => apply(candidate));
          item.appendChild(choice);
          list.appendChild(item);
        }
        results.appendChild(list);
      } catch {
        say("Couldn't reach this server to run the lookup. Check the connection, or enter the coordinates yourself.", true);
      } finally {
        findBtn.disabled = false;
      }
    }

    findBtn.addEventListener("click", find);
    // Enter is what a search box invites. There's no <form> here, so nothing to suppress.
    input.addEventListener("keydown", event => {
      if (event.key === "Enter") find();
    });

    return wrap;
  }

  // A text field whose likely values the server can go and fetch (schema field.optionsSource):
  // OLLAMA's Model box, filled from the models actually pulled on the Ollama machine. Rendered as
  // a dropdown *plus* a way back to the text box, never as a plain select - the list is only what
  // is installed at this moment, and naming a model you are about to pull has to keep working.
  //
  // The <input> stays in the DOM and stays the one thing carrying data-field, so saveSection is
  // untouched by any of this: the dropdown writes into the box, exactly as the "use recommended"
  // button does. If the fetch fails, or this dashboard is talking to a server too old to have the
  // endpoint, what's left on screen is that box - which is all this field ever was before.
  function buildOptionsSource(field, input, loaders) {
    const source = field.optionsSource;

    const wrap = document.createElement("div");
    wrap.className = "field-options";

    const select = document.createElement("select");
    select.className = "field-input";
    select.hidden = true;
    // Deliberately no data-field: two elements answering to the same name would leave saveSection
    // picking whichever came first in the DOM.
    select.dataset.tip = source.tip;

    // The escape hatch's own value. A NUL can't collide with a real model tag, so no list this
    // endpoint returns can ever shadow it.
    const TYPE_IT = "\u0000type-it";

    const back = document.createElement("button");
    back.type = "button";
    back.className = "field-toggle-btn";
    back.textContent = `back to ${source.label}`;
    back.dataset.tip = "Go back to picking from the list.";
    back.setAttribute("data-tip-right", "");
    back.hidden = true;

    const note = document.createElement("p");
    note.className = "field-lookup-note";
    note.hidden = true;

    const row = document.createElement("div");
    row.className = "field-lookup-row";
    row.append(select, input, back);
    wrap.append(row, note);

    let loaded = null;

    function say(message, isError) {
      note.textContent = message || "";
      note.hidden = !message;
      note.classList.toggle("field-lookup-error", !!isError);
    }

    // The box, for a value that isn't in the list - or because there is no list to be had.
    function showBox(message, isError) {
      select.hidden = true;
      input.hidden = false;
      back.hidden = !loaded;
      say(message, isError);
    }

    function showList() {
      select.hidden = false;
      input.hidden = true;
      back.hidden = true;
      say("", false);
    }

    function populate(options) {
      select.textContent = "";
      const current = (input.value || "").trim();

      // Whatever is saved goes in the list even when the server didn't return it - a model pulled
      // on a machine that's currently off, say. Without this, opening the dropdown would quietly
      // land on someone else's model and a later save would keep it.
      const values = options.map(option => option.value);
      const entries = values.includes(current) || current === ""
        ? options
        : [{ value: current, label: `${current} (not listed)` }, ...options];

      for (const option of entries) {
        const opt = document.createElement("option");
        opt.value = option.value;
        opt.textContent = option.label || option.value;
        select.appendChild(opt);
      }

      const typeIt = document.createElement("option");
      typeIt.value = TYPE_IT;
      typeIt.textContent = "type a name instead…";
      select.appendChild(typeIt);

      select.value = current;
    }

    select.addEventListener("change", () => {
      if (select.value === TYPE_IT) {
        showBox(source.typeItMessage, false);
        input.focus();
        input.select();
        return;
      }
      input.value = select.value;
      input.dispatchEvent(new Event("change", { bubbles: true }));
    });

    back.addEventListener("click", () => {
      populate(loaded || []);
      showList();
    });

    async function load() {
      say(`Reading ${source.label}…`, false);
      try {
        const response = await fetch(source.endpoint);
        const payload = await response.json().catch(() => null);
        if (!response.ok) {
          // The server's message is the useful one - it names the address it tried and says what
          // to do instead, which "HTTP 502" does not.
          showBox((payload && payload.error) || `Couldn't list ${source.label} (HTTP ${response.status}).`, true);
          return;
        }
        const options = (payload && payload.options) || [];
        if (options.length === 0) {
          showBox(source.emptyMessage, false);
          return;
        }
        loaded = options;
        populate(options);
        showList();
      } catch {
        showBox(`Couldn't reach this server to list ${source.label}. Type the name instead.`, true);
      }
    }

    // Not fetched here: every section on this page is built up front, and the sections are
    // collapsed, so loading on build would ask Ollama for its model list on every visit to
    // Settings whether or not anyone opens this section. renderSection runs these the first time
    // its section is opened instead.
    if (loaders) loaders.push(load);
    else load();

    return wrap;
  }

  // Must stay identical to SettingsSecrets.Mask on the server: a saved secret is sent out as
  // this, and a save that sends it back untouched means "keep what's stored".
  const SECRET_MASK = "••••••••";

  const SECTION_TIPS = {
    bridge: "How alerts arrive from Blue Iris, and how long they're kept.",
    ollama: "The local AI model that looks at each snapshot, and how it's tuned.",
    cloudVision: "An offsite AI model (OpenAI, Anthropic, or Google) that can analyse alerts instead of, or as a backup for, the local Ollama model.",
    fastLocal: "A lightweight person/vehicle/animal detector for hardware too slow to run a full AI model - sub-second answers on a plain CPU.",
    visionRouting: "Which vision backend handles alerts first, plus fallback on failure and escalation of fast detections to a full AI.",
    imageProcessing: "Resizing snapshots before the AI sees them, to speed up analysis.",
    email: "Email alerts when something notable happens.",
    dailySummary: "A twice-daily digest of everything that happened, separate from individual alerts.",
    telegram: "Push alerts to your phone through a Telegram bot.",
    telegramChat: "Lets you message the bot to ask questions about recent activity.",
    discord: "Post alert cards and trigger snapshots to a Discord channel through a webhook.",
    blueIris: "On-demand Blue Iris snapshots for the Telegram bot. Sentinel's Timeline uses its own local trigger clips instead.",
    multiFrameAnalysis: "Analyze 2-3 frames per trigger instead of one, to reduce false alarms from shadows/reflections. Off by default - costs more AI processing time per alert.",
    triggerRewind: "Saves the frame the motion was really in, rather than a photo taken once the trigger is handled - which on a busy machine can be seconds too late to still show anything.",
    packageTheftWatch: "Flags a package that disappears without a recognized pickup, and forces a notification. Off by default - no extra AI cost, but a heuristic guess, not proof.",
    semanticSearch: "Search the Feed by visual content (\"red truck\") instead of only saved text. Off by default - ~577MB one-time model download, plus per-alert processing time.",
    selfMonitoring: "Watches the bridge itself and warns you if it stops keeping up.",
    quietHours: "Raise the bar for Email, Telegram, and Discord alerts during a window (e.g. overnight) without ever fully muting them.",
    webhook: "Forward each analysed event to another system.",
    mqtt: "Publish events over MQTT with Home Assistant auto-discovery, optionally using this app as its own broker.",
    deviceHub: "Adds a DEVICE HUB tab for switching lights, plugs, and other MQTT devices on and off. Needs MQTT above turned on first.",
    media: "Adds a MOVIES tab: your own movies and TV shows, scanned from folders on the server, with artwork and descriptions from TMDB, playable here and in every app.",
    music: "Adds a MUSIC tab: your own music from folders on the server or another computer, with cover art, lyrics, playlists, radio and stats, played here and in every app. The library folders are set up at the bottom of this section.",
    podcasts: "Adds a PODCASTS tab: shows the server follows, downloads and keeps, with Up Next, transcripts, bookmarks and clips that follow each person to every device.",
    bugReports: "Email the developer when something breaks internally, using the Email account above.",
    loginAlerts: "Email and/or Telegram you when someone enters the wrong password for this dashboard.",
    triggerApi: "Let a doorbell, NVR, or script trigger a camera by fetching a web address.",
    pushNotifications: "Native lock-screen push alerts to the Sentinel Core apps on iPhone, Mac, Apple TV, and Android.",
    logging: "How much detail the server writes to its own log. Applies immediately.",
    server: "The address and port the main bridge service listens on. Takes effect on restart.",
    internetExposed: "A second, dedicated port for reaching this dashboard from outside your home network (e.g. through a Cloudflare Tunnel) - always requires login and HTTPS, regardless of the Login/HTTPS settings above."
  };

  // INTERNET EXPOSED MODE used to be hard-coded here, because it configures this project's own
  // Kestrel binding and so isn't part of the bulk /api/settings snapshot. That hard-coding was
  // also the reason it was the one section the phone and Mac apps never had. It now comes from
  // SettingsSchema.cs like everything else, carrying `dataEndpoint` to say where its values are
  // fetched from and `restartService: "dashboard"` to say who has to restart - both handled
  // generically below, so no client needs to know which section is the odd one.

  function buildField(field, value, loaders) {
    const row = document.createElement("div");
    // A lookup or a dropdown-backed field spans the grid whatever its type: both grow a second
    // line underneath (candidates, or a note about why there's no list), unreadable in a column.
    const wide = WIDE_TYPES.has(field.type) || !!field.lookup || !!field.optionsSource;
    row.className = "field-row" + (wide ? " field-row-wide" : "");
    // The tip lives on the row because <input> can't render the ::after pseudo-element.
    if (field.tip) row.dataset.tip = field.tip;

    if (field.type === "checkbox") {
      row.classList.add("field-checkbox-row");
      const label = document.createElement("label");
      label.className = "switch";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.checked = !!value;
      input.dataset.field = field.name;
      const track = document.createElement("span");
      track.className = "switch-track";
      label.append(input, track);
      const text = document.createElement("span");
      text.className = "field-label";
      text.textContent = field.label;
      row.append(label, text);
      return row;
    }

    const label = document.createElement("label");
    label.className = "field-label";
    label.textContent = field.label;
    row.appendChild(label);

    if (field.type === "password") {
      const wrap = document.createElement("div");
      wrap.className = "field-password-row";
      const input = document.createElement("input");
      input.type = "password";
      input.className = "field-input";
      input.value = value ?? "";
      input.dataset.field = field.name;
      input.autocomplete = "off";
      const toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "field-toggle-btn";
      toggle.setAttribute("data-tip-right", "");

      // A saved secret arrives as SettingsSecrets.Mask and the real value never leaves the
      // server, so while the box still holds the mask the browser has nothing to reveal - a
      // "show" button here swapped eight bullets for eight bullets and read as broken. In that
      // state the box is read-only and the button offers "replace" instead; only once the value
      // is something typed in this session does it become the show/hide toggle. Read-only also
      // stops a click-and-type from producing "••••••••newkey" and saving that as the secret.
      const masked = input.value === SECRET_MASK;
      input.readOnly = masked;

      function syncToggle() {
        if (input.readOnly) {
          toggle.textContent = "replace";
          toggle.dataset.tip = "This secret is saved on the server and never sent back here, so it can't be shown. Click to type a new one - leave it alone to keep what's stored.";
          return;
        }
        toggle.textContent = input.type === "password" ? "show" : "hide";
        toggle.dataset.tip = "Reveal what's typed here so you can check it.";
      }

      toggle.addEventListener("click", () => {
        if (input.readOnly) {
          input.readOnly = false;
          input.value = "";
          input.type = "text";
          input.focus();
        } else {
          input.type = input.type === "password" ? "text" : "password";
        }
        syncToggle();
      });

      // Backing out of a replace: an empty box would save as "clear this secret", so if the
      // field started masked and is left empty, put the mask back rather than letting a change
      // of mind wipe a working key.
      if (masked) {
        input.addEventListener("blur", () => {
          if (input.readOnly || input.value !== "") return;
          input.value = SECRET_MASK;
          input.type = "password";
          input.readOnly = true;
          syncToggle();
        });
      }

      syncToggle();
      wrap.append(input, toggle);
      row.appendChild(wrap);
      return row;
    }

    if (field.type === "csv") {
      const input = document.createElement("input");
      input.type = "text";
      input.className = "field-input";
      input.value = Array.isArray(value) ? value.join(", ") : "";
      input.dataset.field = field.name;
      row.appendChild(input);
      return row;
    }

    if (field.type === "select") {
      const select = document.createElement("select");
      select.className = "field-input";
      select.dataset.field = field.name;
      for (const option of field.options) {
        const opt = document.createElement("option");
        opt.value = option.value;
        opt.textContent = option.label;
        if (option.value === value) opt.selected = true;
        select.appendChild(opt);
      }
      row.appendChild(select);
      return row;
    }

    const input = document.createElement("input");
    input.type = field.type === "number" ? "number" : "text";
    input.className = "field-input";
    input.value = value ?? "";
    input.dataset.field = field.name;
    if (field.type === "number") {
      if (field.min !== undefined) input.min = field.min;
      if (field.max !== undefined) input.max = field.max;
      // A whole-number field steps by 1 rather than "any", so the spinner arrows land on legal
      // values and the browser's own red outline appears on 3.7 in a port box without waiting for
      // save. Fields with no declared step that aren't integers (free space in GB, night-to-day
      // ratio) stay on "any" - fractions are legal there.
      input.step = field.step !== undefined ? field.step : (field.integer ? 1 : "any");
    }
    if (field.lookup) row.appendChild(buildLookup(field, input));
    else if (field.optionsSource) row.appendChild(buildOptionsSource(field, input, loaders));
    else row.appendChild(input);

    // What to put in the box, for the fields where the tip explains what the number does without
    // ever answering that - a confidence threshold means nothing until you have watched this
    // camera misfire for a week. Clickable, because reading a number and then typing it is a
    // silly thing to make someone do.
    if (field.recommended !== undefined && field.recommended !== null) {
      const hint = document.createElement("button");
      hint.type = "button";
      hint.className = "field-recommended";
      hint.textContent = `use recommended: ${field.recommended}`;
      hint.dataset.tip = "Set this field to the value we suggest for a typical install.";
      hint.addEventListener("click", () => {
        input.value = field.recommended;
        input.dispatchEvent(new Event("change", { bubbles: true }));
      });
      row.appendChild(hint);
    }

    return row;
  }

  // Which of the hand-written sections belong in basic mode. The schema sections carry
  // their own `basic` flag; these don't have a schema, so they're listed here instead.
  const BASIC_CUSTOM_SECTIONS = new Set(["APPLE HOMEKIT", "BACKUP", "LOGIN", "LICENSE", "SYSTEM HEALTH"]);

  // Which heading each hand-written section sits under. The schema sections carry `group` and
  // `groupOrder` from the server; these are rendered by hand here and so have to say for
  // themselves. Keep the names character-for-character identical to SettingsGroups in
  // SettingsSchema.cs - a typo doesn't error, it silently opens a second heading with the same
  // name next to the real one.
  const CUSTOM_SECTION_GROUPS = {
    "KNOWN FACES": "AI & Detection",
    "KNOWN VEHICLES": "AI & Detection",
    "PROMPT PROFILES": "AI & Detection",
    "APPLE HOMEKIT": "Automation & Smart Home",
    "SYSTEM HEALTH": "Health & Monitoring",
    "LOGIN": "Access & Network",
    "BACKUP": "Maintenance",
    "LICENSE": "Maintenance"
  };

  // Advanced is opt-in and remembered per device, so the owner can run advanced on a desktop
  // while someone else's phone stays simple. Purely a display preference - nothing here is
  // security-relevant, and every setting is still reachable either way.
  const ADVANCED_MODE_KEY = "gatewatch.settings.advanced";

  function isAdvancedMode() {
    try {
      return localStorage.getItem(ADVANCED_MODE_KEY) === "true";
    } catch {
      // Private browsing and some locked-down setups throw on access rather than returning
      // null. Falling back to basic is the safe default.
      return false;
    }
  }

  function setAdvancedMode(advanced) {
    try {
      localStorage.setItem(ADVANCED_MODE_KEY, advanced ? "true" : "false");
    } catch {
      // Preference just won't persist; the toggle still works for this visit.
    }
  }

  // The current contents of the search box, lowercased. Kept here rather than read from the DOM
  // inside applyMode so the advanced-mode toggle and the search box run through one filter.
  let searchQuery = "";

  // Both filters at once, because they interact: a search has to be able to reach an advanced
  // section while the page is in basic mode, or searching for "mqtt" in basic mode finds nothing
  // and looks broken. So a non-empty search overrides the mode filter entirely - if you typed the
  // word, you want the section, whichever mode you happen to be in.
  function applyMode(advanced) {
    const root = document.getElementById("settings-root");
    const searching = searchQuery.length > 0;

    for (const section of root.querySelectorAll(".settings-section")) {
      const matchesSearch = !searching || (section.dataset.search || "").includes(searchQuery);
      const allowedByMode = advanced || section.dataset.basic === "true";
      section.classList.toggle("hidden", searching ? !matchesSearch : !allowedByMode);
    }

    // A heading with nothing under it reads as a section that failed to load, which matters most
    // while searching - that's when most groups are empty.
    let visibleCount = 0;
    for (const group of root.querySelectorAll(".settings-group")) {
      const anyVisible = [...group.querySelectorAll(".settings-section")]
        .some(section => !section.classList.contains("hidden"));
      group.classList.toggle("hidden", !anyVisible);
      if (anyVisible) visibleCount++;
    }

    const empty = document.getElementById("settings-no-matches");
    if (empty) empty.classList.toggle("hidden", visibleCount > 0);

    // Every section starts collapsed, including the first one. Auto-opening whichever section
    // happened to be first only ever meant BRIDGE was permanently expanded, which reads as a
    // section stuck open rather than as a helpful starting point.

    const label = document.getElementById("mode-label");
    if (label) label.textContent = advanced ? "advanced" : "basic";

    // The MutationObserver in app.js only watches childList, so a class-only change like this
    // won't reposition tooltips on its own.
    window.GateWatchTooltips.schedule();
  }

  // Groups the rendered sections under their headings, in the order the server put the groups in
  // (groupOrder). Sections keep their relative order within a group, which is the order they're
  // declared in SettingsSchema.Sections - the descriptions lean on it ("see VISION ROUTING
  // below"), so it isn't ours to re-sort.
  function groupSections(entries) {
    const groups = new Map();
    for (const entry of entries) {
      const name = entry.group || "Other";
      if (!groups.has(name)) groups.set(name, { name, order: entry.groupOrder, sections: [] });
      const group = groups.get(name);
      // A group's position is the lowest any of its sections claims. Hand-written sections carry
      // no groupOrder at all, so without this a group whose first entry is one of them would sort
      // to the end regardless of where the server puts it.
      if (entry.groupOrder != null && (group.order == null || entry.groupOrder < group.order)) {
        group.order = entry.groupOrder;
      }
      group.sections.push(entry.element);
    }

    const fragment = document.createDocumentFragment();
    const ordered = [...groups.values()].sort((a, b) =>
      (a.order ?? Number.MAX_SAFE_INTEGER) - (b.order ?? Number.MAX_SAFE_INTEGER));

    for (const group of ordered) {
      const wrap = document.createElement("div");
      wrap.className = "settings-group";

      const heading = document.createElement("h2");
      heading.className = "settings-group-heading";
      heading.textContent = group.name;
      wrap.appendChild(heading);

      for (const section of group.sections) wrap.appendChild(section);
      fragment.appendChild(wrap);
    }
    return fragment;
  }

  // Real text rather than a tooltip: the per-field tips are hover-only by design (so a tap
  // doesn't leave one stuck open), which means they're invisible on a phone. This paragraph is
  // what actually orients someone, on any device.
  function buildSectionIntro(text) {
    const intro = document.createElement("p");
    intro.className = "settings-hint settings-section-intro";
    intro.textContent = text;
    return intro;
  }

  function renderSection(schema, data) {
    const section = document.createElement("div");
    section.className = "settings-section";
    if (schema.basic) section.dataset.basic = "true";
    if (schema.group) section.dataset.group = schema.group;
    if (typeof schema.groupOrder === "number") section.dataset.groupOrder = schema.groupOrder;

    // What the search box matches against. The field tips are included deliberately: they're the
    // only place a setting is described in the words someone would actually search for - nobody
    // types "nuisanceAdvisor", they type "spider".
    section.dataset.search = [
      schema.title,
      schema.description,
      ...schema.fields.flatMap(f => [f.label, f.tip])
    ].join(" ").toLowerCase();

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">${schema.title}</span><span class="settings-section-caret">&#9656;</span>`;
    if (SECTION_TIPS[schema.key]) header.dataset.tip = SECTION_TIPS[schema.key];
    // Anything a field wants fetched from the server, deferred until this section is first
    // opened - see buildOptionsSource. Every section on this page is built up front, so doing it
    // any earlier would mean asking Ollama for its model list on every visit to Settings.
    const loaders = [];

    header.addEventListener("click", () => {
      section.classList.toggle("open");
      if (section.classList.contains("open") && loaders.length > 0) {
        for (const load of loaders.splice(0)) load();
      }
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";

    if (schema.description) body.appendChild(buildSectionIntro(schema.description));

    const grid = document.createElement("div");
    grid.className = "field-grid";
    for (const field of schema.fields) {
      grid.appendChild(buildField(field, readPath(data, field.name), loaders));
    }
    body.appendChild(grid);

    // The one section whose usefulness lives outside its own fields: the toggle above only says
    // the buttons may appear, and the server is the only thing that knows whether the operating
    // system will actually permit a reboot. So this section grows a live panel underneath it.
    if (schema.key === "hostReboot") body.appendChild(buildHostRebootPanel());

    // And automatic updates: the fields say what should happen overnight, but which build is
    // running, whether a newer one is out and how the last install went only the server knows.
    if (schema.key === "autoUpdate") body.appendChild(buildAutoUpdatePanel());

    // Same idea, different question: the field above says where the bridge should listen, which
    // is not what someone typing an address into the phone app needs to know. The server is the
    // only side that can see what addresses this machine actually has, so it gets asked.
    if (schema.key === "server") body.appendChild(buildLocalAddressPanel(loaders));

    // MUSIC and PODCASTS each grow a hand-built part under their fields (audio-extras.js): the
    // music library folders and ListenBrainz, and the podcast download folder's picker, storage
    // and OPML. Fetched when the section is first opened, like the other loaders.
    if (schema.key === "music" && window.SentinelAudioExtras) window.SentinelAudioExtras.musicSettingsPanel(body, body, loaders);
    if (schema.key === "podcasts" && window.SentinelAudioExtras) window.SentinelAudioExtras.podcastsSettingsPanel(body, body, loaders);

    const needsRestart = schema.fields.some(f => f.requiresRestart);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    saveBtn.dataset.tip = needsRestart
      ? "Save this section. Fields here only take effect after a restart - use the restart button once saved."
      : "Save this section. Changes take effect immediately - no restart needed.";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn);
    if (schema.extraAction) {
      // Pure data from the schema - an endpoint to POST to - so the same definition works for
      // every client. What comes back is shown rather than thrown away: the test sends report
      // whether the mail server, Telegram or the webhook receiver actually accepted it, and that
      // answer is the entire point of pressing the button. Only an action that says reloadAfter
      // gets a reload (re-running the setup wizard, which can't reappear without one) - this used
      // to reload unconditionally, back when that was the only action there was.
      const actionBtn = document.createElement("button");
      actionBtn.className = "btn";
      actionBtn.textContent = schema.extraAction.label;
      if (schema.extraAction.tip) actionBtn.dataset.tip = schema.extraAction.tip;
      actionBtn.addEventListener("click", async () => {
        const original = actionBtn.textContent;
        actionBtn.disabled = true;
        actionBtn.textContent = "working…";
        showFieldErrors(errors, []);
        try {
          const response = await fetch(schema.extraAction.endpoint, {
            method: schema.extraAction.method || "POST"
          });
          // Endpoints that answer with no body at all are legal - "did it work" is the status.
          const payload = await response.json().catch(() => ({}));

          if (!response.ok) {
            const message = payload.error || `That didn't work (${response.status}).`;
            showFieldErrors(errors, [message]);
            showToast(message, false);
            return;
          }

          if (schema.extraAction.reloadAfter) {
            window.location.reload();
            return;
          }
          showToast(payload.detail || "Done.", true);
        } catch {
          const message = "Could not reach the server.";
          showFieldErrors(errors, [message]);
          showToast(message, false);
        } finally {
          actionBtn.disabled = false;
          actionBtn.textContent = original;
        }
      });
      footer.append(actionBtn);
    }
    if (needsRestart) {
      footer.append(buildRestartButton(schema.restartService || "bridge"));
    }
    footer.append(errors);
    body.appendChild(footer);

    saveBtn.addEventListener("click", () => saveSection(schema, section, errors, saveBtn));

    section.append(header, body);
    return section;
  }

  // Automatic updates' live state, from GET /api/updates/status. Borrows the reboot panel's
  // styling - same job, a status line plus the one action that moves things forward.
  function buildAutoUpdatePanel() {
    const panel = document.createElement("div");
    panel.className = "host-reboot";

    const status = document.createElement("div");
    status.className = "host-reboot-status";
    status.textContent = "checking…";

    const notes = document.createElement("div");
    notes.className = "settings-hint host-reboot-note";
    notes.hidden = true;

    const last = document.createElement("div");
    last.className = "settings-hint host-reboot-note";
    last.hidden = true;

    const actions = document.createElement("div");
    actions.className = "host-reboot-actions";

    const installBtn = document.createElement("button");
    installBtn.type = "button";
    installBtn.className = "btn";
    installBtn.textContent = "install now";
    installBtn.dataset.tip = "Download and install the new release now instead of waiting for tonight. The server restarts for about a minute.";
    installBtn.hidden = true;

    actions.append(installBtn);
    panel.append(status, notes, last, actions);

    let pollTimer = null;
    const when = (iso) => iso ? new Date(iso).toLocaleString() : "";

    function render(s) {
      clearTimeout(pollTimer);
      pollTimer = null;
      const running = `Running build ${s.current.build || "?"} (${s.current.version}, ${s.current.commit}).`;

      if (!s.supported) {
        status.dataset.state = "unsupported";
        status.textContent = `${running} ${s.unsupportedReason}`;
        notes.hidden = true;
        last.hidden = true;
        installBtn.hidden = true;
        return;
      }

      const busy = s.phase && s.phase !== "idle";
      if (busy) {
        status.dataset.state = "pending";
        status.textContent = {
          checking: "Checking for an update…",
          downloading: "Downloading the update…",
          staging: "Unpacking and checking the update…",
          installing: "Installing - the server restarts in a moment, then this page comes back."
        }[s.phase] || "Working…";
        // Keeps asking until the server has restarted onto the new build; a failed fetch while
        // it's down is expected and just means ask again.
        pollTimer = setTimeout(probe, 4000);
      } else if (s.available) {
        status.dataset.state = "setupRequired";
        status.textContent = `${running} Build ${s.available.build} (${s.available.version}) is available` +
          (s.enabled && s.nextWindowUtc ? ` and installs ${when(s.nextWindowUtc)}.` : ".");
      } else {
        status.dataset.state = s.lastCheckOutcome === "failed" ? "unsupported" : (s.enabled ? "ready" : "off");
        const checked = s.lastCheckUtc ? ` Last checked ${when(s.lastCheckUtc)}: ${s.lastMessage}` : " Not checked yet.";
        const next = s.enabled && s.nextWindowUtc ? ` Next overnight check ${when(s.nextWindowUtc)}.` : (s.enabled ? "" : " Automatic installs are off.");
        status.textContent = running + checked + next;
      }

      notes.hidden = !(s.available && s.available.notes);
      notes.textContent = s.available && s.available.notes ? `What's new: ${s.available.notes}` : "";

      const r = s.lastResult;
      last.hidden = !r;
      if (r) {
        const outcome = { installed: "installed", rolledBack: "rolled back", failed: "failed" }[r.outcome] || r.outcome;
        last.textContent = `Last install: build ${r.build} ${outcome} ${when(r.finishedAt)}. ${r.message}`;
      }

      installBtn.hidden = busy || !s.available;
      installBtn.disabled = false;
    }

    async function probe() {
      try {
        const response = await fetch("/api/updates/status");
        if (!response.ok) {
          const payload = await response.json().catch(() => ({}));
          status.dataset.state = "unsupported";
          status.textContent = payload.error || "Could not ask the server about updates.";
          return;
        }
        render(await response.json());
      } catch {
        // Mid-install the server is restarting; keep trying rather than show an error.
        status.dataset.state = "pending";
        status.textContent = "Waiting for the server to come back…";
        pollTimer = setTimeout(probe, 4000);
      }
    }

    installBtn.addEventListener("click", async () => {
      if (!confirm("Install the update now?\n\nThe server restarts for about a minute - live view, " +
        "alerts and this dashboard pause meanwhile. If the new version doesn't start, the current " +
        "one is put back automatically.")) return;
      installBtn.disabled = true;
      try {
        const response = await fetch("/api/updates/install", { method: "POST" });
        const payload = await response.json().catch(() => ({}));
        if (response.ok) {
          showToast(payload.detail || "Installing", true);
        } else {
          showToast(payload.error || "Couldn't start the update", false);
        }
      } catch {
        showToast("Could not reach the server", false);
      }
      setTimeout(probe, 1000);
    });

    probe();
    return panel;
  }

  // Rebooting the machine itself. Three states, and the whole design rests on never conflating
  // them: the feature can be switched off, switched on but not yet permitted by the operating
  // system, or genuinely ready. A green light that turns out to be wrong at the moment you need
  // it is worse than no button at all, so the state shown here always comes from a fresh probe
  // on the server rather than from what the checkbox above happens to say.
  function buildHostRebootPanel() {
    const panel = document.createElement("div");
    panel.className = "host-reboot";

    const status = document.createElement("div");
    status.className = "host-reboot-status";
    status.textContent = "checking…";

    const script = document.createElement("pre");
    script.className = "host-reboot-script";
    script.hidden = true;

    const note = document.createElement("div");
    note.className = "settings-hint host-reboot-note";
    note.hidden = true;

    const actions = document.createElement("div");
    actions.className = "host-reboot-actions";

    const checkBtn = document.createElement("button");
    checkBtn.type = "button";
    checkBtn.className = "btn";
    checkBtn.textContent = "check again";
    checkBtn.dataset.tip = "Ask the server what it can do right now. Nothing is rebooted - this only looks.";

    const copyBtn = document.createElement("button");
    copyBtn.type = "button";
    copyBtn.className = "btn";
    copyBtn.textContent = "copy commands";
    copyBtn.hidden = true;

    const rebootBtn = document.createElement("button");
    rebootBtn.type = "button";
    rebootBtn.className = "btn danger";
    rebootBtn.textContent = "reboot server";
    rebootBtn.hidden = true;

    const cancelBtn = document.createElement("button");
    cancelBtn.type = "button";
    cancelBtn.className = "btn";
    cancelBtn.textContent = "cancel reboot";
    cancelBtn.hidden = true;

    actions.append(checkBtn, copyBtn, rebootBtn, cancelBtn);
    panel.append(status, script, note, actions);

    let countdownTimer = null;

    function stopCountdown() {
      if (countdownTimer) { clearInterval(countdownTimer); countdownTimer = null; }
    }

    // Seeded from the server's own seconds-remaining rather than an absolute timestamp, so a
    // browser with a skewed clock still counts down truthfully.
    function showCountdown(seconds) {
      stopCountdown();
      const due = Date.now() + (Number(seconds) || 0) * 1000;
      const tick = () => {
        const left = Math.round((due - Date.now()) / 1000);
        if (left > 0) {
          status.textContent = `Rebooting in ${left}s. Cancel now to call it off.`;
          status.dataset.state = "pending";
        } else {
          // Past the window the machine is on its way down, and the cancel button would be
          // promising something it cannot deliver - the server refuses it at this point too.
          status.textContent = "Rebooting now - this page will go away for a minute or two.";
          status.dataset.state = "pending";
          cancelBtn.hidden = true;
          stopCountdown();
        }
      };
      // Visibility first, then the interval, then the first tick - seeded with 0 seconds (an
      // install with no undo window, or a screen opened after the window elapsed) tick() hides
      // the cancel button and stops the timer, and doing that before these lines would let them
      // put both straight back for a second while the machine is already going down.
      cancelBtn.hidden = false;
      rebootBtn.hidden = true;
      copyBtn.hidden = true;
      script.hidden = true;
      note.hidden = true;
      countdownTimer = setInterval(tick, 1000);
      tick();
    }

    function render(probe) {
      if (probe.secondsRemaining !== null && probe.secondsRemaining !== undefined) {
        showCountdown(probe.secondsRemaining);
        return;
      }

      stopCountdown();
      cancelBtn.hidden = true;
      status.dataset.state = probe.readiness === "ready" ? (probe.enabled ? "ready" : "off") : probe.readiness;

      const prefix = probe.readiness === "ready" && !probe.enabled
        ? "Ready, but switched off above. Tick Enabled and save to use it. "
        : "";
      status.textContent = `${prefix}${probe.summary} (${probe.platformLabel}, running as ${probe.runningAs})`;

      script.hidden = !probe.setupScript;
      script.textContent = probe.setupScript || "";
      copyBtn.hidden = !probe.setupScript;

      note.hidden = !probe.setupNote;
      note.textContent = probe.setupNote || "";

      // Only offered when it will actually work. An enabled-but-unpermitted install gets the
      // commands instead, which is the thing that moves it forward.
      rebootBtn.hidden = !(probe.enabled && probe.readiness === "ready");
    }

    async function probe() {
      checkBtn.disabled = true;
      try {
        const response = await fetch("/api/system/host-reboot");
        if (!response.ok) {
          const payload = await response.json().catch(() => ({}));
          status.dataset.state = "unsupported";
          status.textContent = payload.error || "Could not ask the server about rebooting.";
          return;
        }
        render(await response.json());
      } catch {
        status.dataset.state = "unsupported";
        status.textContent = "Could not reach the server.";
      } finally {
        checkBtn.disabled = false;
      }
    }

    checkBtn.addEventListener("click", probe);

    copyBtn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(script.textContent);
        showToast("Commands copied - run them on the server, then check again", true);
      } catch {
        // Clipboard access is refused on plain HTTP in some browsers, which is exactly how a
        // LAN dashboard is usually reached. Selecting the text is a workable fallback.
        const range = document.createRange();
        range.selectNodeContents(script);
        const selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(range);
        showToast("Couldn't copy automatically - the commands are selected, press Ctrl+C", false);
      }
    });

    rebootBtn.addEventListener("click", async () => {
      if (!confirm(
        "Reboot the whole server machine now?\n\nEverything stops for a minute or two - analysis, " +
        "this dashboard, notifications. If your recorder is a separate machine it keeps recording " +
        "throughout.")) return;
      rebootBtn.disabled = true;
      try {
        const response = await fetch("/api/system/host-reboot", { method: "POST" });
        const payload = await response.json().catch(() => ({}));
        if (response.status === 202) {
          showToast(payload.message || "Rebooting", true);
          if (payload.secondsRemaining !== null && payload.secondsRemaining !== undefined) {
            showCountdown(payload.secondsRemaining);
          } else {
            probe();
          }
        } else {
          showToast(payload.message || "Could not reboot the server.", false);
          probe();
        }
      } catch {
        showToast("Could not reach the server to reboot it.", false);
      } finally {
        rebootBtn.disabled = false;
      }
    });

    cancelBtn.addEventListener("click", async () => {
      cancelBtn.disabled = true;
      try {
        const response = await fetch("/api/system/host-reboot/cancel", { method: "POST" });
        const payload = await response.json().catch(() => ({}));
        showToast(payload.message || (response.ok ? "Reboot cancelled." : "Could not cancel."), response.ok);
      } catch {
        showToast("Could not reach the server to cancel.", false);
      } finally {
        cancelBtn.disabled = false;
        stopCountdown();
        probe();
      }
    });

    probe();
    return panel;
  }

  // Under SERVER ADDRESS: what this machine's address on the network actually is, so nobody has
  // to go and find it with ifconfig or in the router's client list before they can set a phone
  // up. Read-only - the field above is the setting, this is only ever a report - and asked for
  // when the section is opened rather than on every visit to Settings.
  function buildLocalAddressPanel(loaders) {
    const panel = document.createElement("div");
    panel.className = "local-address";

    const status = document.createElement("div");
    status.className = "local-address-status";
    status.textContent = "checking\u2026";

    const list = document.createElement("ul");
    list.className = "local-address-list";

    const note = document.createElement("p");
    note.className = "settings-hint local-address-note";
    note.hidden = true;

    panel.append(status, list, note);

    // Interface names come from the operating system, and the addresses are assembled by the
    // server, so every one of these goes in as text rather than markup.
    function buildRow(entry) {
      const row = document.createElement("li");

      const url = document.createElement("code");
      url.className = "local-address-url";
      url.textContent = entry.url;

      const label = document.createElement("span");
      label.className = "local-address-interface";
      label.textContent = entry.looksVirtual ? `${entry.interface} (virtual adapter)` : entry.interface;

      const copyBtn = document.createElement("button");
      copyBtn.type = "button";
      copyBtn.className = "btn";
      copyBtn.textContent = "copy";
      copyBtn.dataset.tip = "Copy this address to paste into the phone app or a browser.";
      copyBtn.addEventListener("click", async () => {
        try {
          await navigator.clipboard.writeText(entry.url);
          showToast("Address copied", true);
        } catch {
          // Browsers refuse clipboard access on plain HTTP, which is exactly how a LAN
          // dashboard is usually reached - the same fallback the reboot panel uses.
          const range = document.createRange();
          range.selectNodeContents(url);
          const selection = window.getSelection();
          selection.removeAllRanges();
          selection.addRange(range);
          showToast("Couldn't copy automatically - the address is selected, press Ctrl+C", false);
        }
      });

      row.append(url, label, copyBtn);
      return row;
    }

    function render(payload) {
      const addresses = payload.addresses || [];
      list.replaceChildren(...addresses.map(buildRow));

      if (addresses.length === 0) {
        status.dataset.state = "none";
        status.textContent = "This machine has no network address at the moment, so nothing else on your network can reach it.";
        note.hidden = true;
        return;
      }

      // First in the list is the plain LAN address on a real adapter, if there is one - the
      // server sorts them that way, so this line can just take the head of it.
      const primary = addresses[0];
      status.dataset.state = "ok";
      status.textContent = `This computer is ${payload.hostName} at ${primary.address} - apps and browsers on your network reach it at ${primary.url}.`;

      note.hidden = false;
      note.textContent =
        `Most devices can use ${payload.scheme}://${payload.hostName}.local:${payload.port} instead, which is easier to remember and keeps working if the address changes. ` +
        "That address comes from your router, so it can be a different one after a power cut - give this machine a fixed (reserved) address there if you'd rather it never moved.";
    }

    async function probe() {
      try {
        const response = await fetch("/api/system/addresses");
        if (!response.ok) {
          status.dataset.state = "none";
          status.textContent = "Could not ask the server for its address.";
          return;
        }
        render(await response.json());
      } catch {
        status.dataset.state = "none";
        status.textContent = "Could not reach the server.";
      }
    }

    loaders.push(probe);
    return panel;
  }

  // Sections whose fields only bind at startup get this next to save - the step that used to
  // need SSH. The server refuses (409) when nothing supervises the process, and that refusal is
  // shown as-is: it explains the systemd/Restart:ForceAllowed situation better than a generic
  // message would.
  function buildRestartButton(service) {
    const label = service === "dashboard" ? "restart dashboard" : "restart bridge";
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "btn";
    btn.textContent = label;
    btn.dataset.tip = service === "dashboard"
      ? "Restart this dashboard so saved restart-required settings take effect. The page will be unreachable for a few seconds; running as a service, it comes back on its own."
      : "Restart the main bridge service so saved restart-required settings take effect. Alert processing pauses for a few seconds; running as a service, it comes back on its own.";
    btn.addEventListener("click", async () => {
      const what = service === "dashboard" ? "the dashboard" : "the main bridge service";
      if (!confirm(`Restart ${what} now? It will be unavailable for a few seconds.`)) return;
      btn.disabled = true;
      try {
        const response = await fetch("/api/system/restart", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ service })
        });
        const payload = await response.json().catch(() => ({}));
        if (response.status === 202) {
          showToast(`Restarting ${what} - back in a few seconds`, true);
        } else {
          showToast(payload.error || `Could not restart ${what}.`, false);
        }
      } catch {
        showToast(`Could not reach the server to restart ${what}.`, false);
      } finally {
        btn.disabled = false;
      }
    });
    return btn;
  }

  // What is wrong with what's in a number box, or null if nothing is. Returns the message a user
  // should read rather than a boolean, so the caller can list several at once the way a failed
  // save from the server already does.
  //
  // The empty-string check is doing more work than it looks. A number input whose contents aren't
  // a parseable number - "abc", "1.2.3", a lone "-" - reports value as "" and not as what was
  // typed, so garbage and a genuinely blank box are indistinguishable from .value alone. This
  // used to be read as `input.value === "" ? 0 : ...`, which meant typing letters into Minimum
  // Confidence silently saved 0: not an error anywhere, since 0 is in range, just every alert
  // through the door from then on. validity.badInput is what separates the two cases, and both
  // are now refused rather than one of them being quietly rounded to zero.
  function numberProblem(field, input) {
    const label = field.label || field.name;

    if (input.validity && input.validity.badInput) return `${label} must be a number.`;
    if (input.value.trim() === "") return `${label} must be a number.`;

    const value = Number(input.value);
    if (!Number.isFinite(value)) return `${label} must be a number.`;

    if (field.integer && !Number.isInteger(value)) return `${label} must be a whole number.`;

    const min = field.min;
    const max = field.max;
    if (typeof min === "number" && typeof max === "number" && (value < min || value > max)) {
      return `${label} must be between ${min} and ${max}.`;
    }
    if (typeof min === "number" && value < min) return `${label} must be ${min} or more.`;
    if (typeof max === "number" && value > max) return `${label} must be ${max} or less.`;

    return null;
  }

  async function saveSection(schema, section, errorsEl, saveBtn) {
    const data = {};
    const problems = [];
    for (const field of schema.fields) {
      const input = section.querySelector(`[data-field="${field.name}"]`);
      if (!input) continue;
      let value;
      if (field.type === "checkbox") value = input.checked;
      else if (field.type === "number") {
        const problem = numberProblem(field, input);
        if (problem) {
          problems.push(problem);
          continue;
        }
        value = Number(input.value);
      }
      else if (field.type === "csv") value = input.value.split(",").map(s => s.trim()).filter(s => s.length > 0);
      else value = input.value;
      writePath(data, field.name, value);
    }

    // Caught here rather than left to the server so the message names the box to go and fix, and
    // so a value the server would reject never leaves the page. The server validates all of this
    // again regardless - see SaveSectionAsync - since a client is free to skip these checks.
    if (problems.length > 0) {
      showFieldErrors(errorsEl, problems);
      showToast(`${schema.title}: not saved`, false);
      return;
    }

    await submit(schema.endpoint, data, schema.title, errorsEl, saveBtn);
  }

  function buildQuestionRow(value) {
    const row = document.createElement("div");
    row.className = "question-row";
    const input = document.createElement("input");
    input.type = "text";
    input.className = "field-input";
    input.value = value;
    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "icon-btn";
    removeBtn.textContent = "✕";
    removeBtn.setAttribute("data-tip-right", "");
    removeBtn.dataset.tip = "Remove this question from the profile.";
    removeBtn.addEventListener("click", () => row.remove());
    row.append(input, removeBtn);
    return row;
  }

  function buildProfileCard(name, profile, options) {
    profile = profile || { displayName: "", systemPrompt: "", questions: [], crop: null };
    const startExpanded = !!(options && options.startExpanded);
    const card = document.createElement("div");
    card.className = "profile-card" + (startExpanded ? " open" : "");
    card.dataset.searchText = `${profile.displayName || ""} ${name}`.toLowerCase();

    const header = document.createElement("div");
    header.className = "profile-card-header";
    header.dataset.tip = "Click to expand and edit this profile.";

    const caret = document.createElement("span");
    caret.className = "profile-card-caret";
    caret.textContent = "▸";

    const summary = document.createElement("span");
    summary.className = "profile-card-summary";
    summary.textContent = profile.displayName || name || "(untitled profile)";

    const summaryKey = document.createElement("span");
    summaryKey.className = "profile-card-summary-key";
    summaryKey.textContent = name;

    const deleteBtn = document.createElement("button");
    deleteBtn.type = "button";
    deleteBtn.className = "icon-btn";
    deleteBtn.textContent = "✕";
    deleteBtn.setAttribute("data-tip-right", "");
    deleteBtn.dataset.tip = "Delete this whole profile. The 'default' profile is required and can't be removed.";
    deleteBtn.addEventListener("click", e => {
      e.stopPropagation();
      card.remove();
    });

    header.append(caret, summary, summaryKey, deleteBtn);
    header.addEventListener("click", () => {
      card.classList.toggle("open");
      caret.textContent = card.classList.contains("open") ? "▾" : "▸";
    });

    const body = document.createElement("div");
    body.className = "profile-card-body";

    const displayNameRow = document.createElement("div");
    displayNameRow.className = "field-row";
    displayNameRow.dataset.tip = "Friendly label shown in this list and in the trigger-zone profile picker. Optional - falls back to the technical name below.";
    const displayNameLabel = document.createElement("label");
    displayNameLabel.className = "field-label";
    displayNameLabel.textContent = "Display Name";
    const displayNameInput = document.createElement("input");
    displayNameInput.type = "text";
    displayNameInput.className = "field-input";
    displayNameInput.value = profile.displayName || "";
    displayNameInput.placeholder = "e.g. Chicken Coop Predator Watch";
    displayNameInput.addEventListener("input", () => {
      summary.textContent = displayNameInput.value.trim() || nameInput.value.trim() || "(untitled profile)";
      card.dataset.searchText = `${displayNameInput.value} ${nameInput.value}`.toLowerCase();
    });
    displayNameRow.append(displayNameLabel, displayNameInput);

    const nameRow = document.createElement("div");
    nameRow.className = "field-row";
    nameRow.dataset.tip = "Technical name. Blue Iris and camera profile pickers select a profile by this name, and 'default' is used for anything that doesn't match.";
    const nameLabel = document.createElement("label");
    nameLabel.className = "field-label";
    nameLabel.textContent = "Name (technical)";
    const nameInput = document.createElement("input");
    nameInput.type = "text";
    nameInput.className = "field-input profile-name-input";
    nameInput.value = name;
    nameInput.placeholder = "profile-name";
    nameInput.addEventListener("input", () => {
      summaryKey.textContent = nameInput.value.trim();
      if (!displayNameInput.value.trim()) summary.textContent = nameInput.value.trim() || "(untitled profile)";
      card.dataset.searchText = `${displayNameInput.value} ${nameInput.value}`.toLowerCase();
    });
    nameRow.append(nameLabel, nameInput);

    const promptRow = document.createElement("div");
    promptRow.className = "field-row";
    promptRow.dataset.tip = "Instructions sent to the AI with every image from this camera. Sets what it looks for and how it should reply.";
    const promptLabel = document.createElement("label");
    promptLabel.className = "field-label";
    promptLabel.textContent = "System Prompt";
    const promptArea = document.createElement("textarea");
    promptArea.className = "field-textarea";
    promptArea.rows = 4;
    promptArea.value = profile.systemPrompt || "";
    promptRow.append(promptLabel, promptArea);

    const questionsWrap = document.createElement("div");
    questionsWrap.className = "field-row";
    questionsWrap.dataset.tip = "Specific things to ask about each image. The answers become the structured fields you see on the Events tab.";
    const qLabel = document.createElement("label");
    qLabel.className = "field-label";
    qLabel.textContent = "Questions";
    const qList = document.createElement("div");
    qList.className = "questions-list";
    (profile.questions || []).forEach(q => qList.appendChild(buildQuestionRow(q)));
    const addQBtn = document.createElement("button");
    addQBtn.type = "button";
    addQBtn.className = "icon-btn add";
    addQBtn.textContent = "+";
    addQBtn.dataset.tip = "Add another question for the AI to answer about each image.";
    addQBtn.addEventListener("click", () => qList.appendChild(buildQuestionRow("")));
    questionsWrap.append(qLabel, qList, addQBtn);

    const cropWrap = document.createElement("div");
    cropWrap.className = "field-row";
    cropWrap.dataset.tip = "Send only part of the image to the AI. Useful for cutting out a road or sky so the model focuses on the area you care about.";
    const cropToggleRow = document.createElement("div");
    cropToggleRow.className = "field-checkbox-row";
    const cropSwitch = document.createElement("label");
    cropSwitch.className = "switch";
    const cropInput = document.createElement("input");
    cropInput.type = "checkbox";
    cropInput.checked = !!profile.crop;
    const cropTrack = document.createElement("span");
    cropTrack.className = "switch-track";
    cropSwitch.append(cropInput, cropTrack);
    const cropText = document.createElement("span");
    cropText.className = "field-label";
    cropText.textContent = "Use crop region";
    cropToggleRow.append(cropSwitch, cropText);

    const cropGrid = document.createElement("div");
    cropGrid.className = "crop-grid";
    cropGrid.style.display = profile.crop ? "grid" : "none";
    const crop = profile.crop || { left: 0, top: 0, right: 1, bottom: 1 };
    for (const key of ["left", "top", "right", "bottom"]) {
      const r = document.createElement("div");
      r.className = "field-row";
      const l = document.createElement("label");
      l.className = "field-label";
      l.textContent = key;
      const i = document.createElement("input");
      i.type = "number";
      i.className = "field-input";
      i.dataset.cropField = key;
      i.min = 0;
      i.max = 1;
      i.step = 0.01;
      i.value = crop[key] ?? (key === "right" || key === "bottom" ? 1 : 0);
      r.append(l, i);
      cropGrid.appendChild(r);
    }
    cropInput.addEventListener("change", () => {
      cropGrid.style.display = cropInput.checked ? "grid" : "none";
    });
    cropWrap.append(cropToggleRow, cropGrid);

    const plateWrap = document.createElement("div");
    plateWrap.className = "field-row field-checkbox-row";
    plateWrap.dataset.tip = "Also ask the AI to transcribe a vehicle's license plate when clearly legible, and check it against the Known Vehicles list below. Off by default - needs a camera with enough resolution and angle to actually read plates, and never affects whether an alert fires.";
    const plateSwitch = document.createElement("label");
    plateSwitch.className = "switch";
    const plateInput = document.createElement("input");
    plateInput.type = "checkbox";
    plateInput.checked = !!profile.licensePlateRecognitionEnabled;
    const plateTrack = document.createElement("span");
    plateTrack.className = "switch-track";
    plateSwitch.append(plateInput, plateTrack);
    const plateText = document.createElement("span");
    plateText.className = "field-label";
    plateText.textContent = "License plate recognition";
    plateWrap.append(plateSwitch, plateText);

    const faceWrap = document.createElement("div");
    faceWrap.className = "field-row field-checkbox-row";
    faceWrap.dataset.tip = "Also run local face detection/matching against the Known Faces list below, entirely on-device (no image is sent anywhere for this). Off by default - never affects whether an alert fires.";
    const faceSwitch = document.createElement("label");
    faceSwitch.className = "switch";
    const faceInput = document.createElement("input");
    faceInput.type = "checkbox";
    faceInput.checked = !!profile.faceRecognitionEnabled;
    const faceTrack = document.createElement("span");
    faceTrack.className = "switch-track";
    faceSwitch.append(faceInput, faceTrack);
    const faceText = document.createElement("span");
    faceText.className = "field-label";
    faceText.textContent = "Face recognition";
    faceWrap.append(faceSwitch, faceText);

    body.append(displayNameRow, nameRow, promptRow, questionsWrap, cropWrap, plateWrap, faceWrap);
    card.append(header, body);
    card._fields = { displayNameInput, nameInput, promptArea, qList, cropInput, cropGrid, plateInput, faceInput };
    return card;
  }

  function collectProfile(card) {
    const f = card._fields;
    const name = f.nameInput.value.trim();
    const displayName = f.displayNameInput.value.trim();
    const systemPrompt = f.promptArea.value.trim();
    const questions = Array.from(f.qList.querySelectorAll(".question-row input"))
      .map(i => i.value.trim())
      .filter(v => v.length > 0);

    // Blank or unparseable crop edges are caught here because they are the one crop mistake the
    // server can't describe well: parseFloat gives NaN, JSON.stringify writes it as null, and the
    // save comes back as a model-binding failure rather than as the readable rule the endpoint
    // has for everything else about a crop. Out-of-range and back-to-front edges are left to that
    // rule, which says the whole of it in one sentence.
    const problems = [];
    let crop = null;
    if (f.cropInput.checked) {
      crop = {};
      for (const input of f.cropGrid.querySelectorAll("[data-crop-field]")) {
        const edge = input.dataset.cropField;
        const value = parseFloat(input.value);
        if (!Number.isFinite(value)) {
          problems.push(`Profile '${name || "(untitled)"}': crop ${edge} must be a number between 0 and 1.`);
          continue;
        }
        crop[edge] = value;
      }
    }

    const licensePlateRecognitionEnabled = f.plateInput.checked;
    const faceRecognitionEnabled = f.faceInput.checked;

    return {
      name,
      problems,
      profile: { displayName, systemPrompt, questions, crop, licensePlateRecognitionEnabled, faceRecognitionEnabled }
    };
  }

  function buildKnownVehicleRow(vehicle) {
    vehicle = vehicle || { plate: "", label: "" };
    const row = document.createElement("div");
    row.className = "field-row-wide known-vehicle-row";

    const plateInput = document.createElement("input");
    plateInput.type = "text";
    plateInput.className = "field-input";
    plateInput.placeholder = "plate, e.g. ABC1234";
    plateInput.value = vehicle.plate || "";

    const labelInput = document.createElement("input");
    labelInput.type = "text";
    labelInput.className = "field-input";
    labelInput.placeholder = "label, e.g. Morgan's Truck";
    labelInput.value = vehicle.label || "";

    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "icon-btn";
    removeBtn.textContent = "✕";
    removeBtn.setAttribute("data-tip-right", "");
    removeBtn.dataset.tip = "Remove this vehicle.";
    removeBtn.addEventListener("click", () => row.remove());

    row.append(plateInput, labelInput, removeBtn);
    row._fields = { plateInput, labelInput };
    return row;
  }

  function renderBackupSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">BACKUP</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Download a complete backup - every setting, camera, schedule, account, Device Hub device and Organizer item with its attachments - or restore from one. For a copy every night, see NIGHTLY AUTO EXPORT.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Saves everything you'd otherwise have to set up again into one file. That's settings, cameras, camera groups, trigger zones, recording schedules, gate checks, user accounts, Device Hub devices, automation rules and schedules. It's also all Organizer notes, events, reminders and boards with their attachments, known faces with their photos, the licence, and this server's HTTPS certificate authority and HomeKit pairing. Restore it here, on this machine or a replacement. To have this done for you every night, and sent to another computer, turn on NIGHTLY AUTO EXPORT. The FILES library itself is not in this file - only its backup drives are; the drives hold the files, and a restored server brings them back from one with \"bring files back\" on the FILES tab."));

    const warning = document.createElement("div");
    warning.className = "settings-hint";
    warning.textContent = "Includes secrets (passwords, API keys, tokens) and every user account's stored password for a complete restore - anyone with this file can sign in to a restored copy as any of your users, so store it like a password, or give it a passphrase below. Restoring overlays onto your current setup; it never deletes a camera or setting the backup doesn't mention.";
    body.appendChild(warning);

    // The two things about restoring accounts that are surprising in the moment rather than in
    // hindsight: other people get signed out, and your own login deliberately doesn't change -
    // which is what stops a backup with a forgotten admin password locking you out mid-restore.
    const accountsNote = document.createElement("div");
    accountsNote.className = "settings-hint";
    accountsNote.textContent = "Restoring brings back user accounts and signs those people out, so they'll log in again with the password from the backup. Your own account is left exactly as it is. Event history, captured images and recorded video aren't included.";
    body.appendChild(accountsNote);

    // How the last nightly export went - the one place the result of a run nobody watched shows up.
    const autoStatus = document.createElement("div");
    autoStatus.className = "settings-hint";
    autoStatus.hidden = true;
    body.appendChild(autoStatus);
    loadAutoExportStatus(autoStatus);

    const passphraseRow = document.createElement("div");
    passphraseRow.className = "field-row field-row-wide";
    passphraseRow.dataset.tip = "Optional. Encrypts the full backup you download, and is what you enter to restore an encrypted one. At least 8 characters. There's no way to recover a file whose passphrase is lost.";
    const passphraseLabel = document.createElement("label");
    passphraseLabel.className = "field-label";
    passphraseLabel.textContent = "Passphrase (optional)";
    const passphraseInput = document.createElement("input");
    passphraseInput.type = "password";
    passphraseInput.className = "field-input";
    passphraseInput.autocomplete = "new-password";
    passphraseRow.append(passphraseLabel, passphraseInput);
    body.appendChild(passphraseRow);

    const exportRow = document.createElement("div");
    exportRow.className = "profiles-toolbar";
    const bundleBtn = document.createElement("button");
    bundleBtn.className = "btn";
    bundleBtn.textContent = "download full backup";
    bundleBtn.dataset.tip = "Everything, including Organizer attachments, known-face photos, the licence and this server's certificates, as one .zip - or an encrypted .scbackup if you've entered a passphrase above.";
    const exportBtn = document.createElement("button");
    exportBtn.className = "btn";
    exportBtn.textContent = "export settings only (.json)";
    exportBtn.dataset.tip = "The configuration and Organizer items as a JSON file, without the attached files or certificates - the same file the phone and Mac apps save. Never encrypted.";
    exportRow.append(bundleBtn, exportBtn);
    body.appendChild(exportRow);

    const importRow = document.createElement("div");
    importRow.className = "field-row field-row-wide";
    const importLabel = document.createElement("label");
    importLabel.className = "field-label";
    importLabel.textContent = "Restore from backup";
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = ".json,.zip,.scbackup";
    fileInput.id = "backup-file-input";
    importRow.append(importLabel, fileInput);
    body.appendChild(importRow);

    // Off by default: on a server that's working, replacing its certificate authority means every
    // device has to trust it again. On a replacement for a lost server it's what saves that.
    const identityRow = document.createElement("div");
    identityRow.className = "field-row field-checkbox-row";
    identityRow.dataset.tip = "Tick this when restoring onto a new machine that replaces a lost one. It brings back the old server's HTTPS certificate authority, so phones and browsers that trusted it trust this one, and its HomeKit pairing, so the Home app finds the bridge it already knows. Both need a restart afterwards. Leave it off when rolling back on a server that's working - replacing its certificates would make every device trust it again from scratch.";
    const identitySwitch = document.createElement("label");
    identitySwitch.className = "switch";
    const identityInput = document.createElement("input");
    identityInput.type = "checkbox";
    const identityTrack = document.createElement("span");
    identityTrack.className = "switch-track";
    identitySwitch.append(identityInput, identityTrack);
    const identityText = document.createElement("span");
    identityText.className = "field-label";
    identityText.textContent = "This is a replacement server (restore its certificates and HomeKit pairing)";
    identityRow.append(identitySwitch, identityText);
    body.appendChild(identityRow);

    const errors = document.createElement("ul");
    errors.className = "field-errors";
    body.appendChild(errors);

    function saveBlob(blob, name) {
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = name;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    }

    exportBtn.addEventListener("click", async () => {
      exportBtn.disabled = true;
      try {
        const response = await fetch("/api/backup/export");
        if (!response.ok) {
          showToast("Export failed", false);
          return;
        }
        const data = await response.json();
        saveBlob(new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }),
          `sentinelcore-backup-${new Date().toISOString().slice(0, 10)}.json`);
        showToast(data.warnings && data.warnings.length ? `Downloaded, but: ${data.warnings.join(" ")}` : "Backup downloaded", !(data.warnings && data.warnings.length));
      } catch {
        showToast("Export failed", false);
      } finally {
        exportBtn.disabled = false;
      }
    });

    bundleBtn.addEventListener("click", async () => {
      errors.innerHTML = "";
      const passphrase = passphraseInput.value;
      if (passphrase && passphrase.length < 8) {
        showFieldErrors(errors, ["Use a passphrase of at least 8 characters, or leave it blank."]);
        return;
      }
      bundleBtn.disabled = true;
      const original = bundleBtn.textContent;
      bundleBtn.textContent = "building…";
      try {
        const response = await fetch("/api/backup/bundle", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ passphrase: passphrase || null })
        });
        if (!response.ok) {
          const payload = await response.json().catch(() => ({}));
          showFieldErrors(errors, [payload.error || "Could not build the backup."]);
          showToast("Backup failed", false);
          return;
        }
        const disposition = response.headers.get("Content-Disposition") || "";
        const match = /filename="?([^";]+)"?/.exec(disposition);
        saveBlob(await response.blob(), match ? match[1] : `sentinelcore-backup.${passphrase ? "scbackup" : "zip"}`);
        showToast("Full backup downloaded", true);
      } catch {
        showToast("Backup failed", false);
      } finally {
        bundleBtn.disabled = false;
        bundleBtn.textContent = original;
      }
    });

    fileInput.addEventListener("change", async () => {
      const file = fileInput.files[0];
      if (!file) return;
      errors.innerHTML = "";
      try {
        // Sent as raw bytes - the server works out from the contents whether it's JSON, a zip or
        // an encrypted bundle. The passphrase goes in a header, never the URL.
        const headers = { "Content-Type": "application/octet-stream" };
        if (passphraseInput.value) headers["X-Backup-Passphrase"] = passphraseInput.value;
        const query = identityInput.checked ? "?machineIdentity=true" : "";
        const response = await fetch(`/api/backup/import${query}`, { method: "POST", headers, body: file });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          if (payload.passphraseRequired) passphraseInput.focus();
          showFieldErrors(errors, payload.errors || ["Could not restore that file."]);
          showToast("Restore failed", false);
          return;
        }
        // Only the parts that actually restored something, so a backup with no groups or gate
        // checks doesn't report "0 camera group(s)" and read like something went missing.
        const restored = [
          [payload.camerasRestored, "camera"],
          [payload.cameraGroupsRestored, "camera group"],
          [payload.triggerZonesRestored, "trigger zone"],
          [payload.recordingSchedulesRestored, "recording schedule"],
          [payload.gateChecksRestored, "gate check"],
          [payload.accountsRestored, "account"],
          [payload.devicesRestored, "device"],
          [payload.automationRulesRestored, "automation rule"],
          [payload.schedulesRestored, "schedule"],
          [payload.organizerItemsRestored, "Organizer item"],
          [payload.attachmentsRestored, "attachment"],
          [payload.knownFacePhotosRestored, "face photo"]
        ]
          .filter(([count]) => count > 0)
          .map(([count, noun]) => `${count} ${noun}${count === 1 ? "" : "s"}`);
        const summary = restored.length ? `Restored ${restored.join(", ")}` : "Restored settings";
        // Notes are things to act on (restart this, that part was skipped) - they stay on screen
        // rather than vanishing with a toast and a reload.
        if (payload.notes && payload.notes.length) {
          showFieldErrors(errors, [summary + ".", ...payload.notes]);
          showToast(summary, true);
          return;
        }
        showToast(summary, true);
        window.location.reload();
      } catch {
        showFieldErrors(errors, ["Network error while restoring."]);
      } finally {
        fileInput.value = "";
      }
    });

    section.append(header, body);
    return section;
  }

  async function loadAutoExportStatus(el) {
    try {
      const response = await fetch("/api/backup/auto/status");
      if (!response.ok) return;
      const status = await response.json();
      if (!status.enabled && !status.lastAttemptUtc) return;
      const when = (iso) => new Date(iso).toLocaleString();
      const parts = [];
      if (status.lastAttemptUtc) {
        const label = { ok: "succeeded", incomplete: "was saved but incomplete", failed: "failed" }[status.lastResult] || status.lastResult;
        parts.push(`Last ${status.lastTrigger === "manual" ? "manual" : "nightly"} export (${when(status.lastAttemptUtc)}) ${label}: ${status.lastMessage}`);
        if (status.lastWarnings && status.lastWarnings.length) parts.push(status.lastWarnings.join(" "));
      }
      if (status.enabled && status.nextRunUtc) parts.push(`Next nightly export: ${when(status.nextRunUtc)}.`);
      else if (!status.enabled) parts.push("Nightly auto export is off.");
      el.textContent = parts.join(" ");
      el.hidden = false;
    } catch {
      // The status line is a nicety; the section works without it.
    }
  }

  // Custom rather than a generic SECTIONS entry because the useful half of this section isn't
  // config at all - it's the pairing QR, which comes from the sidecar rather than appsettings.
  // The config fields themselves still go through buildField/submit like everywhere else.
  function renderHomeKitSection(data) {
    data = data || {};

    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">APPLE HOMEKIT</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Show your cameras and Device Hub devices in the Apple Home app, and get iPhone notifications when an alert fires. Scan the pairing code below with your iPhone. Live video isn't included - HomeKit only gets motion alerts.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Puts your cameras in Apple's Home app as motion sensors, so alert-worthy events become native iPhone notifications - and, with 'include device hub' on, your MQTT lights, switches and gates as accessories you can control from Home. Live video isn't included - that needs Apple's paid HomeKit Secure Video licensing - but everything else works. Nothing is put on your network until you turn this on. A HomeKit sensor is silent until you turn on Allow Notifications for it in the Home app."));

    const fields = [
      {
        name: "enabled", label: "enabled", type: "checkbox",
        tip: "Send a motion alert to Apple Home whenever an alert is worth notifying you about. Needs the HomeKit sidecar service running."
      },
      {
        name: "includeDevices", label: "include device hub", type: "checkbox",
        tip: "Also put your Device Hub devices in Apple Home - lights, switches, outlets, fans, locks and gates become real HomeKit accessories you can control and use in Home automations. Needs MQTT and the Device Hub turned on."
      },
      {
        name: "sidecarBaseUrl", label: "sidecar url", type: "text",
        tip: "Where the HomeKit sidecar's control API is listening. The default is right unless you changed its port."
      },
      {
        name: "minimumConfidence", label: "minimum confidence", type: "number", min: 0, max: 1, step: 0.05,
        tip: "How sure the AI must be before this trips a HomeKit motion alert on your phone. Raise it if you get woken by false alarms; lower it if real events are being missed. Quiet Hours can raise this further overnight."
      }
    ];

    const grid = document.createElement("div");
    grid.className = "field-grid";
    for (const field of fields) grid.appendChild(buildField(field, data[field.name]));
    body.appendChild(grid);

    // --- pairing panel ---
    const pairing = document.createElement("div");
    pairing.className = "field-row-wide";

    const status = document.createElement("div");
    status.className = "settings-hint";
    status.textContent = "checking HomeKit sidecar…";
    pairing.appendChild(status);

    const diagnostics = document.createElement("div");
    diagnostics.className = "settings-hint";
    pairing.appendChild(diagnostics);

    const qrWrap = document.createElement("div");
    qrWrap.className = "homekit-qr";
    pairing.appendChild(qrWrap);

    body.appendChild(pairing);

    async function refreshStatus() {
      qrWrap.innerHTML = "";
      try {
        const response = await fetch("/api/homekit/status");
        const payload = await response.json();

        if (!payload.running) {
          status.textContent = payload.error || "The HomeKit sidecar isn't running.";
          diagnostics.textContent = "";
          return;
        }
        const lastTrigger = payload.lastTriggerAt
          ? ` Last motion sent for ${payload.lastTriggerCamera || "a camera"} at ${new Date(payload.lastTriggerAt).toLocaleString()}.`
          : " No motion has been sent since this sidecar started.";
        diagnostics.textContent = `HomeKit endpoint: TCP ${payload.hapPort || 51826}; advertiser: ${payload.advertiser || "unknown"}.${lastTrigger}`;
        if (!payload.enabled) {
          // The sidecar is installed and healthy, it just isn't advertising anything yet -
          // nothing is on your network until the toggle above goes on.
          status.textContent = "turned off - switch enabled on and save to get a pairing code.";
          return;
        }
        if (payload.paired) {
          const names = (payload.accessories || []).join(", ");
          status.textContent = names
            ? `paired - sending motion for: ${names}`
            : "paired - cameras appear here after their first alert.";
          return;
        }

        status.textContent = "not paired yet - open the Home app on your iPhone, tap +, then Add Accessory, and scan this code:";

        const img = document.createElement("img");
        // Cache-busted so re-pairing after a sidecar reset never shows a stale code.
        img.src = `/api/homekit/qr?t=${Date.now()}`;
        img.alt = "HomeKit pairing QR code";
        img.className = "homekit-qr-image";
        qrWrap.appendChild(img);

        if (payload.pairingCode) {
          const code = document.createElement("div");
          code.className = "homekit-pairing-code";
          // Some people type the code in rather than scanning, and the Home app asks for it
          // by hand if the camera can't focus on the screen.
          code.textContent = payload.pairingCode;
          qrWrap.appendChild(code);
        }
      } catch {
        status.textContent = "could not check the HomeKit sidecar.";
      }
    }

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    saveBtn.dataset.tip = "Save this section. Changes take effect immediately - no restart needed.";
    const errors = document.createElement("ul");
    errors.className = "field-errors";

    const refreshBtn = document.createElement("button");
    refreshBtn.className = "btn";
    refreshBtn.textContent = "refresh";
    refreshBtn.dataset.tip = "Re-check the sidecar's pairing status.";
    refreshBtn.addEventListener("click", () => refreshStatus());

    const resetBtn = document.createElement("button");
    resetBtn.className = "btn";
    resetBtn.textContent = "reset pairing";
    resetBtn.dataset.tip = "Create a completely new HomeKit bridge identity when Apple Home says the existing accessory is not responding. You will need to remove the old bridge from Home and scan a new QR code.";
    resetBtn.addEventListener("click", async () => {
      const confirmed = window.confirm(
        "This erases Sentinel Core's HomeKit pairing and creates a new bridge identity. Remove the old Sentinel Core bridge from Apple Home, then click OK. You will need to scan a new QR code."
      );
      if (!confirmed) return;

      resetBtn.disabled = true;
      errors.innerHTML = "";
      try {
        const response = await fetch("/api/homekit/reset", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ confirm: "RESET" })
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showFieldErrors(errors, [payload.error || "Could not reset HomeKit pairing."]);
          return;
        }

        status.textContent = "pairing reset - waiting for the HomeKit service to restart…";
        diagnostics.textContent = "";
        // systemd/Windows Service restarts the sidecar. Wait for the new unpaired identity and
        // QR rather than making the user guess when it is ready.
        for (let attempt = 0; attempt < 20; attempt++) {
          await new Promise(resolve => setTimeout(resolve, 1500));
          await refreshStatus();
          if (section.querySelector(".homekit-qr-image")) {
            showToast("HomeKit pairing reset - scan the new code", true);
            return;
          }
        }
        showFieldErrors(errors, ["Pairing was reset, but the sidecar has not restarted yet. Restart the HomeKit service, then press refresh."]);
      } catch {
        showFieldErrors(errors, ["Network error while resetting HomeKit pairing."]);
      } finally {
        resetBtn.disabled = false;
      }
    });

    footer.append(saveBtn, refreshBtn, resetBtn, errors);
    body.appendChild(footer);

    saveBtn.addEventListener("click", async () => {
      // minimumConfidence is rendered above and so has to be read back here. This section builds
      // its payload by hand rather than through saveSection - it is the one section with a QR
      // pairing panel and no schema entry - and the field was missing from that list, which meant
      // editing it did nothing and every save quietly wrote the DTO's own default over it.
      const confidenceField = fields.find(f => f.name === "minimumConfidence");
      const confidenceInput = section.querySelector('[data-field="minimumConfidence"]');
      const problem = numberProblem(confidenceField, confidenceInput);
      if (problem) {
        showFieldErrors(errors, [problem]);
        showToast("Apple HomeKit: not saved", false);
        return;
      }

      const payload = {
        enabled: section.querySelector('[data-field="enabled"]').checked,
        includeDevices: section.querySelector('[data-field="includeDevices"]').checked,
        sidecarBaseUrl: section.querySelector('[data-field="sidecarBaseUrl"]').value,
        minimumConfidence: Number(confidenceInput.value),
        timeoutSeconds: data.timeoutSeconds || 10
      };
      await submit("/api/settings/homekit", payload, "Apple HomeKit", errors, saveBtn);

      // The sidecar re-reads the config on its own schedule, so the QR isn't ready the instant
      // this returns. Poll briefly rather than showing "turned off" to someone who just turned
      // it on and is waiting for a code to scan.
      if (payload.enabled) {
        status.textContent = "starting HomeKit…";
        for (let attempt = 0; attempt < 8; attempt++) {
          await new Promise(resolve => setTimeout(resolve, 2000));
          await refreshStatus();
          if (section.querySelector(".homekit-qr-image")) return;
        }
      }
      await refreshStatus();
    });

    refreshStatus();

    section.append(header, body);
    return section;
  }

  const SYSTEM_STATUS_POLL_MS = 5_000;

  function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit++;
    }
    return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
  }

  function formatUptime(seconds) {
    const days = Math.floor(seconds / 86400);
    const hours = Math.floor((seconds % 86400) / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    if (days > 0) return `${days}d ${hours}h`;
    if (hours > 0) return `${hours}h ${minutes}m`;
    return `${minutes}m`;
  }

  function barSeverityClass(percent) {
    if (percent >= 90) return "danger";
    if (percent >= 70) return "warn";
    return "";
  }

  function buildStatBar(labelText, percent, detailText) {
    const row = document.createElement("div");
    row.className = "system-stat-row";

    const label = document.createElement("div");
    label.className = "system-stat-label";
    label.innerHTML = `<span>${labelText}</span><strong>${detailText}</strong>`;

    const track = document.createElement("div");
    track.className = "system-stat-bar";
    const fill = document.createElement("div");
    fill.className = `system-stat-bar-fill ${barSeverityClass(percent)}`.trim();
    fill.style.width = `${Math.min(100, Math.max(0, percent)).toFixed(0)}%`;
    track.appendChild(fill);

    row.append(label, track);
    return row;
  }

  function buildStatPlain(labelText, detailText) {
    const row = document.createElement("div");
    row.className = "system-stat-plain";
    row.innerHTML = `<span>${labelText}</span><strong>${detailText}</strong>`;
    return row;
  }

  // Read-only telemetry, not configuration - no save button, just a live snapshot polled on
  // its own interval so it stays current without reloading the whole settings form (which
  // would blow away any in-progress edits elsewhere on the page).
  function renderSystemStatusSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">SYSTEM HEALTH</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "How hard this server is working right now - CPU, memory, disk, and the analysis queue. Read-only; nothing here is configuration.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("How hard this server is currently working. Useful for checking whether it's keeping up before you add more cameras or lean on cloud vision less."));

    const grid = document.createElement("div");
    grid.className = "system-status-grid";
    grid.innerHTML = `<div class="empty-state">checking system status&hellip;</div>`;
    body.appendChild(grid);

    async function refresh() {
      // Only while somebody can actually see it. Every poll has the server sample its own CPU,
      // memory and disk, and this used to run every five seconds for the life of the page whether
      // the section was open, collapsed, or on a tab nobody had visited in hours.
      if (!section.classList.contains("open") || section.offsetParent === null) return;
      try {
        const response = await fetch("/api/system/status");
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const s = await response.json();

        grid.innerHTML = "";
        grid.appendChild(buildStatBar("CPU", s.cpuUsagePercent, `${s.cpuUsagePercent.toFixed(0)}% of ${s.processorCount} core${s.processorCount === 1 ? "" : "s"}`));
        const memPercent = s.memoryTotalBytes > 0 ? (s.memoryUsedBytes / s.memoryTotalBytes) * 100 : 0;
        grid.appendChild(buildStatBar("Memory", memPercent, `${formatBytes(s.memoryUsedBytes)} / ${formatBytes(s.memoryTotalBytes)}`));
        const diskUsed = s.diskTotalBytes - s.diskFreeBytes;
        const diskPercent = s.diskTotalBytes > 0 ? (diskUsed / s.diskTotalBytes) * 100 : 0;
        grid.appendChild(buildStatBar("Disk", diskPercent, `${formatBytes(diskUsed)} / ${formatBytes(s.diskTotalBytes)}`));
        grid.appendChild(buildStatPlain("Analysis queue", `${s.queuedAlerts} pending`));
        grid.appendChild(buildStatPlain("Vision backend", s.visionBackendHealthy ? "reachable" : "unreachable"));
        grid.appendChild(buildStatPlain("Uptime", formatUptime(s.uptimeSeconds)));
      } catch {
        grid.innerHTML = `<div class="empty-state">could not load system status.</div>`;
      }
    }

    // Started on first open rather than at build time, and refreshed straight away on every open
    // so the numbers are never up to five seconds stale when the section unfolds.
    let pollTimer = null;
    header.addEventListener("click", () => {
      if (!section.classList.contains("open")) return;
      refresh();
      if (!pollTimer) pollTimer = setInterval(refresh, SYSTEM_STATUS_POLL_MS);
    });

    section.append(header, body);
    return section;
  }

  function renderLicenseSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">LICENSE</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Install the .lic file you received after purchase. Missing or invalid only shows a notice - every feature keeps working either way.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Installs the licence file you were emailed after purchase. Missing or invalid shows a notice and a watermark, and nothing else - every feature keeps working either way, so this is never urgent."));

    const status = document.createElement("div");
    status.className = "settings-hint";
    status.textContent = "checking license…";
    body.appendChild(status);

    const row = document.createElement("div");
    row.className = "field-row-wide";
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = ".lic";
    fileInput.id = "license-file-input";
    row.appendChild(fileInput);
    body.appendChild(row);

    const errors = document.createElement("ul");
    errors.className = "field-errors";
    body.appendChild(errors);

    async function refreshStatus() {
      try {
        const response = await fetch("/api/license/status");
        const payload = await response.json();
        status.textContent = payload.valid
          ? `licensed to ${payload.customerName}`
          : "no valid license installed - running unlicensed.";
      } catch {
        status.textContent = "could not check license status.";
      }
    }

    fileInput.addEventListener("change", async () => {
      const file = fileInput.files[0];
      if (!file) return;
      errors.innerHTML = "";
      status.textContent = "installing…";
      try {
        const contents = await file.text();
        const response = await fetch("/api/license/install", {
          method: "POST",
          headers: { "Content-Type": "text/plain" },
          body: contents
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showFieldErrors(errors, [payload.error || "Could not install that file."]);
          showToast("License not installed", false);
        } else {
          showToast(`Licensed to ${payload.customerName}`, true);
          window.GateWatchLicense.check();
        }
      } catch {
        showFieldErrors(errors, ["Network error while uploading."]);
      } finally {
        fileInput.value = "";
        await refreshStatus();
      }
    });

    refreshStatus();

    section.append(header, body);
    return section;
  }

  function renderAuthSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">LOGIN</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Require a username and password to open this dashboard. Off by default - anyone who can reach this network can otherwise use it freely.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Requires a username and password to open this dashboard. Off by default, which is fine on a home network you trust - but turn it on before this is reachable from anywhere else. Sessions live in memory, so everyone is signed out whenever the app restarts."));

    const status = document.createElement("div");
    status.className = "settings-hint";
    status.textContent = "checking login status…";
    body.appendChild(status);

    const formWrap = document.createElement("div");
    body.appendChild(formWrap);

    const errors = document.createElement("ul");
    errors.className = "field-errors";
    body.appendChild(errors);

    function renderEnableForm() {
      formWrap.innerHTML = `
        <div class="field-row field-row-wide">
          <label class="field-label">Username</label>
          <input type="text" class="field-input" id="auth-new-username" autocomplete="username" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Password</label>
          <input type="password" class="field-input" id="auth-new-password" autocomplete="new-password" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Confirm Password</label>
          <input type="password" class="field-input" id="auth-new-confirm" autocomplete="new-password" />
        </div>
        <button class="btn" id="auth-enable-btn">require login</button>`;

      document.getElementById("auth-enable-btn").addEventListener("click", async () => {
        errors.innerHTML = "";
        const username = document.getElementById("auth-new-username").value.trim();
        const password = document.getElementById("auth-new-password").value;
        const confirmPassword = document.getElementById("auth-new-confirm").value;
        if (password !== confirmPassword) {
          showFieldErrors(errors, ["Passwords do not match."]);
          return;
        }
        try {
          const response = await fetch("/api/auth/enable", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username, password })
          });
          const payload = await response.json().catch(() => ({}));
          if (!response.ok) {
            showFieldErrors(errors, payload.errors || ["Could not enable login."]);
            showToast("Login not enabled", false);
            return;
          }
          showToast("Login required", true);
          window.GateWatchAuth.check();
          await refreshStatus();
        } catch {
          showFieldErrors(errors, ["Network error while saving."]);
        }
      });
    }

    function renderChangeForm(currentUsername) {
      formWrap.innerHTML = `
        <div class="field-row field-row-wide">
          <label class="field-label">Current Password</label>
          <input type="password" class="field-input" id="auth-current-password" autocomplete="current-password" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Username</label>
          <input type="text" class="field-input" id="auth-username" value="${escapeHtml(currentUsername)}" autocomplete="username" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">New Password (leave blank to keep current)</label>
          <input type="password" class="field-input" id="auth-password" autocomplete="new-password" />
        </div>
        <div class="field-row field-row-wide">
          <label class="field-label">Confirm New Password</label>
          <input type="password" class="field-input" id="auth-confirm" autocomplete="new-password" />
        </div>
        <div class="settings-section-footer">
          <button class="btn" id="auth-update-btn">save</button>
          <button class="btn" id="auth-disable-btn">turn off login</button>
        </div>`;

      document.getElementById("auth-update-btn").addEventListener("click", async () => {
        errors.innerHTML = "";
        const currentPassword = document.getElementById("auth-current-password").value;
        const newUsername = document.getElementById("auth-username").value.trim();
        const newPassword = document.getElementById("auth-password").value;
        const confirmNewPassword = document.getElementById("auth-confirm").value;
        if (newPassword !== confirmNewPassword) {
          showFieldErrors(errors, ["Passwords do not match."]);
          return;
        }
        try {
          const response = await fetch("/api/auth/update", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ currentPassword, newUsername, newPassword: newPassword || null })
          });
          const payload = await response.json().catch(() => ({}));
          if (!response.ok) {
            showFieldErrors(errors, payload.errors || ["Could not save."]);
            showToast("Not saved", false);
            return;
          }
          showToast("Login updated", true);
          await refreshStatus();
        } catch {
          showFieldErrors(errors, ["Network error while saving."]);
        }
      });

      document.getElementById("auth-disable-btn").addEventListener("click", async () => {
        if (!confirm("Turn off login? Anyone on the network will then have full access to this dashboard.")) return;
        try {
          const response = await fetch("/api/auth/disable", { method: "POST" });
          if (!response.ok) {
            showToast("Could not turn off login", false);
            return;
          }
          showToast("Login turned off", true);
          window.GateWatchAuth.check();
          await refreshStatus();
        } catch {
          showFieldErrors(errors, ["Network error while saving."]);
        }
      });
    }

    async function refreshStatus() {
      errors.innerHTML = "";
      try {
        const response = await fetch("/api/auth/status");
        const payload = await response.json();
        if (payload.enabled) {
          status.textContent = "login is required to open this dashboard.";
          renderChangeForm(payload.username || "");
        } else {
          status.textContent = "no login required - open to anyone on the network.";
          renderEnableForm();
        }
      } catch {
        status.textContent = "could not check login status.";
      }
    }

    refreshStatus();

    section.append(header, body);
    return section;
  }

  function renderKnownVehiclesSection(vehicles) {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">KNOWN VEHICLES</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "Vehicles checked against a transcribed plate, for cameras/profiles with license plate recognition turned on. Informational only - a match or non-match never changes whether an alert fires.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Vehicles you recognise, matched against plates the AI reads. This is informational only - a match or non-match is noted in the alert but never changes whether one fires. Only does anything for cameras using a prompt profile with licence plate recognition turned on."));

    const hint = document.createElement("div");
    hint.className = "settings-hint";
    hint.textContent = "Shared across every camera/profile that has license plate recognition enabled.";
    body.appendChild(hint);

    const rowsWrap = document.createElement("div");
    rowsWrap.className = "known-vehicles-list";
    for (const vehicle of vehicles || []) {
      rowsWrap.appendChild(buildKnownVehicleRow(vehicle));
    }
    body.appendChild(rowsWrap);

    const toolbar = document.createElement("div");
    toolbar.className = "profiles-toolbar";
    const addBtn = document.createElement("button");
    addBtn.className = "btn";
    addBtn.textContent = "+ add vehicle";
    addBtn.dataset.tip = "Add another known vehicle by plate and label.";
    addBtn.addEventListener("click", () => rowsWrap.appendChild(buildKnownVehicleRow(null)));
    toolbar.appendChild(addBtn);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    saveBtn.dataset.tip = "Save the known vehicle list.";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn, errors);

    saveBtn.addEventListener("click", async () => {
      const list = Array.from(rowsWrap.querySelectorAll(".known-vehicle-row"))
        .map(row => ({ plate: row._fields.plateInput.value.trim(), label: row._fields.labelInput.value.trim() }))
        .filter(v => v.plate.length > 0);
      await submit("/api/settings/known-vehicles", list, "Known vehicles", errors, saveBtn);
    });

    body.append(toolbar, footer);
    section.append(header, body);
    return section;
  }

  function renderKnownFacesSection() {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">KNOWN FACES</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "People recognized from a photo, for cameras/profiles with face recognition turned on. Runs entirely on-device - informational only, a match or non-match never changes whether an alert fires.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("People you recognize, matched against faces detected in alert photos using a local, on-device model - no image ever leaves your network for this. Informational only - a match or non-match is noted in the alert but never changes whether one fires. Only does anything for cameras using a prompt profile with face recognition turned on."));

    const listWrap = document.createElement("div");
    listWrap.className = "known-faces-list";
    body.appendChild(listWrap);

    const errors = document.createElement("ul");
    errors.className = "field-errors";

    async function refresh() {
      listWrap.innerHTML = `<div class="empty-state">loading&hellip;</div>`;
      try {
        const response = await fetch("/api/known-faces");
        const list = response.ok ? await response.json() : [];
        listWrap.innerHTML = "";
        if (list.length === 0) {
          listWrap.innerHTML = `<div class="empty-state">No known faces enrolled yet.</div>`;
        }
        for (const face of list) listWrap.appendChild(buildKnownFaceCard(face, refresh, errors));
      } catch {
        listWrap.innerHTML = `<div class="empty-state">Could not load known faces.</div>`;
      }
    }

    const addRow = document.createElement("div");
    addRow.className = "field-row-wide known-vehicle-row";
    const labelInput = document.createElement("input");
    labelInput.type = "text";
    labelInput.className = "field-input";
    labelInput.placeholder = "name, e.g. Morgan";
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = "image/*";
    const addBtn = document.createElement("button");
    addBtn.className = "btn";
    addBtn.textContent = "+ add person";
    addBtn.dataset.tip = "Enroll a new known person from a clear, front-facing photo of just them.";
    addBtn.addEventListener("click", async () => {
      errors.innerHTML = "";
      const label = labelInput.value.trim();
      const file = fileInput.files[0];
      if (!label) { showFieldErrors(errors, ["A name is required."]); return; }
      if (!file) { showFieldErrors(errors, ["Choose a photo first."]); return; }

      addBtn.disabled = true;
      const originalText = addBtn.textContent;
      addBtn.textContent = "adding...";
      try {
        const form = new FormData();
        form.append("label", label);
        form.append("image", file);
        const response = await fetch("/api/known-faces", { method: "POST", body: form });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showFieldErrors(errors, [payload.error || "Could not add that person."]);
          showToast("Known faces: not saved", false);
        } else {
          labelInput.value = "";
          fileInput.value = "";
          showToast("Known faces saved", true);
          await refresh();
        }
      } catch {
        showFieldErrors(errors, ["Network error while adding."]);
      } finally {
        addBtn.disabled = false;
        addBtn.textContent = originalText;
      }
    });
    addRow.append(labelInput, fileInput, addBtn);

    body.append(addRow, errors);
    section.append(header, body);
    refresh();
    return section;
  }

  function buildKnownFaceCard(face, onChange, sharedErrors) {
    const card = document.createElement("div");
    card.className = "profile-card";

    const header = document.createElement("div");
    header.className = "profile-card-header";
    header.style.cursor = "default";
    const name = document.createElement("span");
    name.className = "profile-card-summary";
    name.textContent = face.label;
    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "icon-btn";
    removeBtn.textContent = "✕";
    removeBtn.setAttribute("data-tip-right", "");
    removeBtn.dataset.tip = `Remove ${face.label} and all their enrolled photos.`;
    removeBtn.addEventListener("click", async () => {
      if (!confirm(`Remove ${face.label} from known faces?`)) return;
      try {
        const response = await fetch(`/api/known-faces/${face.id}`, { method: "DELETE" });
        if (!response.ok) {
          const payload = await response.json().catch(() => ({}));
          showFieldErrors(sharedErrors, [response.status === 403
            ? "Only an admin account can remove a known face."
            : (payload.error || "Could not remove that person.")]);
          return;
        }
      } catch {
        showFieldErrors(sharedErrors, ["Network error while removing."]);
        return;
      }
      await onChange();
    });
    header.append(name, removeBtn);

    const photos = document.createElement("div");
    photos.className = "known-face-photos";
    for (const enrollment of face.enrollments) {
      const thumbWrap = document.createElement("div");
      thumbWrap.className = "known-face-photo";
      const img = document.createElement("img");
      img.src = enrollment.photoUrl;
      img.alt = face.label;
      const del = document.createElement("button");
      del.type = "button";
      del.className = "icon-btn known-face-photo-remove";
      del.textContent = "✕";
      del.dataset.tip = "Remove this photo.";
      del.addEventListener("click", async () => {
        await fetch(`/api/known-faces/${face.id}/enrollments/${enrollment.id}`, { method: "DELETE" });
        await onChange();
      });
      thumbWrap.append(img, del);
      photos.appendChild(thumbWrap);
    }

    const addPhotoInput = document.createElement("input");
    addPhotoInput.type = "file";
    addPhotoInput.accept = "image/*";
    addPhotoInput.className = "known-face-add-photo";
    addPhotoInput.dataset.tip = "Add another reference photo of this person, for better matching.";
    addPhotoInput.addEventListener("change", async () => {
      const file = addPhotoInput.files[0];
      if (!file) return;
      const form = new FormData();
      form.append("image", file);
      const response = await fetch(`/api/known-faces/${face.id}/enrollments`, { method: "POST", body: form });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        showFieldErrors(sharedErrors, [payload.error || "Could not add that photo."]);
      } else {
        await onChange();
      }
    });

    card.append(header, photos, addPhotoInput);
    return card;
  }

  function renderPromptProfilesSection(profiles) {
    const section = document.createElement("div");
    section.className = "settings-section";

    const header = document.createElement("div");
    header.className = "settings-section-header";
    header.innerHTML = `<span class="settings-section-title">PROMPT PROFILES</span><span class="settings-section-caret">&#9656;</span>`;
    header.dataset.tip = "What the AI is told to look for on each camera - what matters, what to ignore, and what's worth alerting you about. Around a hundred ready-made profiles are included.";
    header.addEventListener("click", () => {
      section.classList.toggle("open");
      window.GateWatchTooltips.schedule();
    });

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.appendChild(buildSectionIntro("Prompt profiles are the instructions given to the AI for each camera - what to look at, what to ignore, and what counts as worth alerting you about. Around a hundred ready-made profiles ship with the app covering common camera placements; pick one per trigger zone rather than writing your own unless you have a specific need."));

    const count = Object.keys(profiles || {}).length;
    const hint = document.createElement("div");
    hint.className = "settings-hint";
    hint.textContent = `The AI prompts sent to Ollama per camera profile (${count} available). A 'default' profile is required as a fallback for unrecognized profile names. Click a profile to expand it.`;
    body.appendChild(hint);

    const searchRow = document.createElement("div");
    searchRow.className = "field-row profiles-search-row";
    const searchInput = document.createElement("input");
    searchInput.type = "text";
    searchInput.className = "field-input";
    searchInput.placeholder = "search profiles by name (e.g. \"chicken\", \"gate\", \"garage\")…";
    searchRow.appendChild(searchInput);
    body.appendChild(searchRow);

    const cardsWrap = document.createElement("div");
    cardsWrap.className = "settings-root";
    for (const [name, profile] of Object.entries(profiles || {})) {
      cardsWrap.appendChild(buildProfileCard(name, profile));
    }

    searchInput.addEventListener("input", () => {
      const term = searchInput.value.trim().toLowerCase();
      for (const card of cardsWrap.querySelectorAll(".profile-card")) {
        const matches = !term || card.dataset.searchText.includes(term);
        card.classList.toggle("filtered-out", !matches);
      }
    });

    const toolbar = document.createElement("div");
    toolbar.className = "profiles-toolbar";
    const addBtn = document.createElement("button");
    addBtn.className = "btn";
    addBtn.textContent = "+ add profile";
    addBtn.dataset.tip = "Create another prompt profile. Blue Iris picks one per camera by name.";
    addBtn.addEventListener("click", () => {
      const card = buildProfileCard("", null, { startExpanded: true });
      cardsWrap.appendChild(card);
      card.scrollIntoView({ behavior: "smooth", block: "center" });
    });
    toolbar.appendChild(addBtn);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "save";
    saveBtn.dataset.tip = "Save every profile. New prompts apply to the next alert that comes in.";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn, errors);

    saveBtn.addEventListener("click", async () => {
      const dict = {};
      const problems = [];
      for (const card of cardsWrap.querySelectorAll(".profile-card")) {
        const { name, profile, problems: cardProblems } = collectProfile(card);
        problems.push(...cardProblems);
        if (name) dict[name] = profile;
      }
      if (problems.length > 0) {
        showFieldErrors(errors, problems);
        showToast("Prompt profiles: not saved", false);
        return;
      }
      await submit("/api/settings/prompt-profiles", dict, "Prompt profiles", errors, saveBtn);
    });

    body.append(cardsWrap, toolbar, footer);
    section.append(header, body);
    return section;
  }

  async function submit(endpoint, body, label, errorsEl, saveBtn) {
    errorsEl.innerHTML = "";
    saveBtn.disabled = true;
    const originalText = saveBtn.textContent;
    saveBtn.textContent = "saving...";
    try {
      const response = await fetch(endpoint, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        showFieldErrors(errorsEl, payload.errors || ["Save failed."]);
        showToast(`${label}: not saved`, false);
      } else {
        showToast(`${label} saved`, true);
        // The Movies tab only exists while the section is on - show or hide it now.
        if (endpoint === "/api/settings/media") window.GateWatchAuth.checkMovies();
        // Same for the Music and Podcasts tabs.
        if ((endpoint === "/api/settings/music" || endpoint === "/api/settings/podcasts") && window.SentinelAudio) window.SentinelAudio.checkEnabled();
      }
    } catch {
      showFieldErrors(errorsEl, ["Network error while saving."]);
      showToast("Save failed", false);
    } finally {
      saveBtn.disabled = false;
      saveBtn.textContent = originalText;
    }
  }

  function showFieldErrors(el, errors) {
    el.innerHTML = "";
    for (const message of errors) {
      const li = document.createElement("li");
      li.textContent = message;
      el.appendChild(li);
    }
  }

  let toastTimeout;
  function showToast(message, success) {
    const toast = document.getElementById("toast");
    clearTimeout(toastTimeout);
    toast.textContent = message;
    toast.className = "toast " + (success ? "success" : "error");
    toastTimeout = setTimeout(() => { toast.className = "toast hidden"; }, 3000);
  }

  async function load() {
    const root = document.getElementById("settings-root");
    root.innerHTML = `<div class="empty-state">loading settings&hellip;</div>`;
    try {
      const [settingsResponse, schemaResponse] = await Promise.all([
        fetch("/api/settings"),
        fetch("/api/settings/schema")
      ]);
      if (!settingsResponse.ok) throw new Error(`HTTP ${settingsResponse.status}`);
      if (!schemaResponse.ok) throw new Error(`HTTP ${schemaResponse.status} loading the settings schema`);
      const data = await settingsResponse.json();
      const sections = await schemaResponse.json();

      root.innerHTML = "";

      // A section can say its values live somewhere other than the bulk snapshot (dataEndpoint).
      // Only INTERNET EXPOSED MODE does today; fetching them together keeps that from adding a
      // round trip per section if more ever do. A failed fetch yields null, which renders the
      // section with empty values rather than dropping it - the same thing an absent snapshot key
      // already does.
      const external = new Map();
      await Promise.all(sections.filter(s => s.dataEndpoint).map(async schema => {
        const value = await fetch(schema.dataEndpoint)
          .then(r => r.ok ? r.json() : null)
          .catch(() => null);
        external.set(schema.key, value);
      }));

      const entries = sections.map(schema => ({
        element: renderSection(schema, schema.dataEndpoint ? external.get(schema.key) : data[schema.key]),
        group: schema.group,
        groupOrder: schema.groupOrder
      }));

      // The hand-written sections: built the same way as before, then tagged with the group and
      // basic flag the schema would have carried for them.
      for (const element of [
        renderSystemStatusSection(),
        renderHomeKitSection(data.homeKit),
        renderBackupSection(),
        renderAuthSection(),
        renderLicenseSection(),
        renderKnownVehiclesSection(data.knownVehicles),
        renderKnownFacesSection(),
        renderPromptProfilesSection(data.promptProfiles)
      ]) {
        const title = element.querySelector(".settings-section-title")?.textContent ?? "";
        if (BASIC_CUSTOM_SECTIONS.has(title)) element.dataset.basic = "true";
        // No schema to build a field list from, so the rendered text is what search matches on.
        // Good enough - these sections are short, and their words are on screen.
        element.dataset.search = element.textContent.toLowerCase();
        entries.push({ element, group: CUSTOM_SECTION_GROUPS[title] });
      }

      root.appendChild(groupSections(entries));

      const empty = document.createElement("div");
      empty.id = "settings-no-matches";
      empty.className = "empty-state hidden";
      empty.textContent = "no settings match that search.";
      root.appendChild(empty);

      const toggle = document.getElementById("advanced-mode-toggle");
      if (toggle) {
        toggle.checked = isAdvancedMode();
        toggle.onchange = () => {
          setAdvancedMode(toggle.checked);
          applyMode(toggle.checked);
        };
      }

      // Same key the inline script in index.html reads at page load.
      const kioskToggle = document.getElementById("kiosk-mode-toggle");
      if (kioskToggle) {
        kioskToggle.checked = document.documentElement.classList.contains("kiosk");
        kioskToggle.onchange = () => {
          document.documentElement.classList.toggle("kiosk", kioskToggle.checked);
          try {
            localStorage.setItem("sentinelcore.kiosk", kioskToggle.checked ? "true" : "false");
          } catch {
            // Preference just won't persist; the toggle still works for this visit.
          }
        };
      }

      // Searching matches field labels and tips, not just section titles, so "spider" finds the
      // advisor and "port" finds every section that binds one. Not persisted like the mode
      // toggle: a filter still applied on the next visit would be a page with settings missing.
      const search = document.getElementById("settings-search");
      if (search) {
        search.value = "";
        search.oninput = () => {
          searchQuery = search.value.trim().toLowerCase();
          applyMode(isAdvancedMode());
        };
      }

      applyMode(isAdvancedMode());
    } catch (err) {
      root.innerHTML = `<div class="empty-state">failed to load settings: ${err.message}</div>`;
    }
  }

  return { load };
})();
