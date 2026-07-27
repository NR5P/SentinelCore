window.GateWatchAccounts = (function () {
  let me = null; // { id, username, role } from /api/auth/status
  let accounts = [];
  let editingNewAccount = false;

  function escapeHtml(value) {
    const div = document.createElement("div");
    div.textContent = value ?? "";
    return div.innerHTML;
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
    try {
      const statusResponse = await fetch("/api/auth/status");
      const status = await statusResponse.json();
      me = { username: status.username, role: status.role };
    } catch (err) {
      console.error("Failed to load auth status", err);
      me = null;
    }

    if (me?.role !== "Admin") return;

    try {
      const response = await fetch("/api/accounts");
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      accounts = await response.json();
    } catch (err) {
      console.error("Failed to load accounts", err);
    }
  }

  function renderList() {
    const root = document.getElementById("accounts-root");
    root.innerHTML = "";

    root.appendChild(buildMyAccountSection());

    if (me?.role === "Admin") {
      root.appendChild(buildAllAccountsSection());
    } else {
      const note = document.createElement("div");
      note.className = "empty-state";
      note.textContent = "Only admins can create or manage other accounts.";
      root.appendChild(note);
    }
  }

  // ---------------- My account (any signed-in user) ----------------

  function buildMyAccountSection() {
    const section = document.createElement("div");
    section.className = "settings-section open";
    section.innerHTML = `
      <div class="settings-section-header">
        <span class="settings-section-title">MY ACCOUNT</span>
      </div>
      <div class="settings-section-body" style="display:block;">
        <div class="field-grid" id="my-account-fields"></div>
      </div>
    `;

    const fields = section.querySelector("#my-account-fields");
    if (me?.username) {
      const info = document.createElement("div");
      info.className = "field-row field-row-wide";
      info.innerHTML = `<label class="field-label">Signed in as</label><div>${escapeHtml(me.username)} (${escapeHtml(me.role ?? "Admin")})</div>`;
      fields.appendChild(info);
    }

    const currentPassword = buildPasswordInput("my-current-password", "Current password");
    const newPassword = buildPasswordInput("my-new-password", "New password (min. 8 characters)");
    fields.append(currentPassword, newPassword);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "change password";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn, errors);
    section.appendChild(footer);

    saveBtn.addEventListener("click", async () => {
      errors.innerHTML = "";
      const body = {
        currentPassword: section.querySelector("#my-current-password").value,
        newPassword: section.querySelector("#my-new-password").value
      };
      try {
        const response = await fetch("/api/accounts/change-password", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body)
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showErrors(errors, payload.errors || ["Could not change the password."]);
          return;
        }
        section.querySelector("#my-current-password").value = "";
        section.querySelector("#my-new-password").value = "";
        showToast("Password changed.", true);
      } catch {
        showErrors(errors, ["Network error while changing the password."]);
      }
    });

    return section;
  }

  // ---------------- All accounts (admin only) ----------------

  function buildAllAccountsSection() {
    const section = document.createElement("div");
    section.className = "settings-section open";
    section.innerHTML = `
      <div class="settings-section-header">
        <span class="settings-section-title">ALL ACCOUNTS</span>
      </div>
    `;

    const body = document.createElement("div");
    body.className = "settings-section-body";
    body.style.display = "block";

    const toolbar = document.createElement("div");
    toolbar.className = "toolbar-actions";
    const addBtn = document.createElement("button");
    addBtn.className = "btn";
    addBtn.textContent = "+ add account";
    addBtn.addEventListener("click", () => {
      editingNewAccount = true;
      renderList();
    });
    toolbar.appendChild(addBtn);
    body.appendChild(toolbar);

    if (editingNewAccount) {
      body.appendChild(buildNewAccountForm());
    }

    if (accounts.length === 0) {
      const empty = document.createElement("div");
      empty.className = "empty-state";
      empty.textContent = "no accounts found";
      body.appendChild(empty);
    } else {
      for (const account of accounts) {
        body.appendChild(buildAccountCard(account));
      }
    }

    section.appendChild(body);
    return section;
  }

  function buildNewAccountForm() {
    const wrap = document.createElement("div");
    wrap.className = "camera-card";

    const fields = document.createElement("div");
    fields.className = "field-grid";
    fields.appendChild(buildTextInput("new-account-username", "Username"));
    fields.appendChild(buildPasswordInput("new-account-password", "Password (min. 8 characters)"));
    fields.appendChild(buildTextInput("new-account-display-name", "Display name (optional)"));
    fields.appendChild(buildRoleSelect("new-account-role"));
    wrap.appendChild(fields);

    const footer = document.createElement("div");
    footer.className = "settings-section-footer";
    const saveBtn = document.createElement("button");
    saveBtn.className = "btn";
    saveBtn.textContent = "create";
    const cancelBtn = document.createElement("button");
    cancelBtn.className = "btn";
    cancelBtn.textContent = "cancel";
    const errors = document.createElement("ul");
    errors.className = "field-errors";
    footer.append(saveBtn, cancelBtn, errors);
    wrap.appendChild(footer);

    cancelBtn.addEventListener("click", () => {
      editingNewAccount = false;
      renderList();
    });

    saveBtn.addEventListener("click", async () => {
      errors.innerHTML = "";
      const body = {
        username: wrap.querySelector("#new-account-username").value.trim(),
        password: wrap.querySelector("#new-account-password").value,
        displayName: wrap.querySelector("#new-account-display-name").value.trim() || null,
        role: wrap.querySelector("#new-account-role").value
      };
      try {
        const response = await fetch("/api/accounts", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body)
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showErrors(errors, payload.errors || ["Could not create the account."]);
          return;
        }
        editingNewAccount = false;
        await load();
        renderList();
      } catch {
        showErrors(errors, ["Network error while creating the account."]);
      }
    });

    return wrap;
  }

  function buildAccountCard(account) {
    const card = document.createElement("div");
    card.className = "camera-card";

    const info = document.createElement("div");
    info.className = "camera-card-info";
    info.innerHTML = `
      <span class="camera-card-name">${escapeHtml(account.username)}${account.displayName ? ` (${escapeHtml(account.displayName)})` : ""}</span>
      <span class="camera-card-meta">${escapeHtml(account.role)}</span>
    `;

    const errors = document.createElement("ul");
    errors.className = "field-errors";

    const actions = document.createElement("div");
    actions.className = "camera-card-actions";

    const otherRole = account.role === "Admin" ? "Viewer" : "Admin";
    const roleBtn = document.createElement("button");
    roleBtn.className = "btn";
    roleBtn.textContent = `make ${otherRole.toLowerCase()}`;
    roleBtn.addEventListener("click", async () => {
      errors.innerHTML = "";
      try {
        const response = await fetch(`/api/accounts/${account.id}/role`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ role: otherRole })
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showErrors(errors, payload.errors || ["Could not change the role."]);
          return;
        }
        await load();
        renderList();
      } catch {
        showErrors(errors, ["Network error while changing the role."]);
      }
    });

    const resetBtn = document.createElement("button");
    resetBtn.className = "btn";
    resetBtn.textContent = "reset password";
    resetBtn.addEventListener("click", async () => {
      errors.innerHTML = "";
      const newPassword = prompt(`New password for "${account.username}" (min. 8 characters):`);
      if (!newPassword) return;
      try {
        const response = await fetch(`/api/accounts/${account.id}/reset-password`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ newPassword })
        });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showErrors(errors, payload.errors || ["Could not reset the password."]);
          return;
        }
        showToast(`Password reset for ${account.username}.`, true);
      } catch {
        showErrors(errors, ["Network error while resetting the password."]);
      }
    });

    const deleteBtn = document.createElement("button");
    deleteBtn.className = "btn";
    deleteBtn.textContent = "delete";
    deleteBtn.addEventListener("click", async () => {
      errors.innerHTML = "";
      if (!confirm(`Delete account "${account.username}"? This cannot be undone.`)) return;
      try {
        const response = await fetch(`/api/accounts/${account.id}`, { method: "DELETE" });
        const payload = await response.json().catch(() => ({}));
        if (!response.ok) {
          showErrors(errors, payload.errors || ["Could not delete the account."]);
          return;
        }
        await load();
        renderList();
      } catch {
        showErrors(errors, ["Network error while deleting the account."]);
      }
    });

    actions.append(roleBtn, resetBtn, deleteBtn);
    card.append(info, actions, errors);
    return card;
  }

  // ---------------- Shared field builders ----------------

  function buildTextInput(id, label) {
    const row = document.createElement("div");
    row.className = "field-row field-row-wide";
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "text";
    input.className = "field-input";
    input.id = id;
    row.appendChild(input);
    return row;
  }

  function buildPasswordInput(id, label) {
    const row = document.createElement("div");
    row.className = "field-row field-row-wide";
    row.innerHTML = `<label class="field-label" for="${id}">${label}</label>`;
    const input = document.createElement("input");
    input.type = "password";
    input.className = "field-input";
    input.id = id;
    input.autocomplete = "off";
    row.appendChild(input);
    return row;
  }

  function buildRoleSelect(id) {
    const row = document.createElement("div");
    row.className = "field-row field-row-wide";
    row.innerHTML = `<label class="field-label" for="${id}">Role</label>`;
    const select = document.createElement("select");
    select.className = "field-input";
    select.id = id;
    for (const role of ["Viewer", "Admin"]) {
      const opt = document.createElement("option");
      opt.value = role;
      opt.textContent = role;
      select.appendChild(opt);
    }
    row.appendChild(select);
    return row;
  }

  function showErrors(list, messages) {
    list.innerHTML = "";
    for (const message of messages) {
      const li = document.createElement("li");
      li.textContent = message;
      list.appendChild(li);
    }
  }

  return { load, renderList };
})();
