window.GateWatchAuth = (function () {
  const overlay = document.getElementById("login-gate");
  const usernameInput = document.getElementById("login-username");
  const passwordInput = document.getElementById("login-password");
  const submitBtn = document.getElementById("login-submit-btn");
  const errorsEl = document.getElementById("login-errors");
  const logoutBtn = document.getElementById("logout-btn");
  const accountsTab = document.getElementById("tab-accounts");
  const settingsTab = document.getElementById("tab-settings");
  const devicesTab = document.getElementById("tab-devices");
  const titleEl = document.getElementById("login-title");
  const introEl = document.getElementById("login-intro");
  const confirmRow = document.getElementById("login-confirm-row");
  const confirmInput = document.getElementById("login-confirm");

  // A fresh install has no accounts, and the server refuses everything until the first admin
  // exists - so the same overlay doubles as "create your admin account" (see AuthEndpoints'
  // /api/auth/enable).
  let creatingFirstAdmin = false;

  function useFirstAdminMode() {
    creatingFirstAdmin = true;
    titleEl.textContent = "CREATE ADMIN ACCOUNT";
    introEl.textContent = "Welcome to Sentinel Core. Choose the username and password for this server's admin account - you'll use them to sign in here and in the apps.";
    confirmRow.classList.remove("hidden");
    passwordInput.autocomplete = "new-password";
    submitBtn.textContent = "create account";
  }

  function showErrors(messages) {
    errorsEl.innerHTML = "";
    for (const message of messages) {
      const li = document.createElement("li");
      li.textContent = message;
      errorsEl.appendChild(li);
    }
  }

  async function submit() {
    showErrors([]);
    if (creatingFirstAdmin && passwordInput.value !== confirmInput.value) {
      showErrors(["Passwords do not match."]);
      return;
    }
    submitBtn.disabled = true;
    try {
      const response = await fetch(creatingFirstAdmin ? "/api/auth/enable" : "/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username: usernameInput.value.trim(), password: passwordInput.value })
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        if (response.status === 429) showErrors(["Too many attempts - wait a few minutes and try again."]);
        else showErrors(payload.errors || [payload.error || (creatingFirstAdmin ? "Could not create the account." : "Sign-in failed.")]);
        return;
      }
      // Simplest way to resume normal init with a fresh, authenticated set of requests -
      // mirrors the setup wizard's own completion behavior (see wizard.js advance()).
      window.location.reload();
    } catch {
      showErrors(["Network error while signing in."]);
    } finally {
      submitBtn.disabled = false;
    }
  }

  submitBtn.addEventListener("click", submit);
  passwordInput.addEventListener("keydown", (e) => {
    if (e.key !== "Enter") return;
    if (creatingFirstAdmin) confirmInput.focus(); else submit();
  });
  confirmInput.addEventListener("keydown", (e) => { if (e.key === "Enter") submit(); });
  usernameInput.addEventListener("keydown", (e) => { if (e.key === "Enter") passwordInput.focus(); });

  logoutBtn.addEventListener("click", async () => {
    await fetch("/api/auth/logout", { method: "POST" });
    window.location.reload();
  });

  async function checkDeviceHub() {
    try {
      const response = await fetch("/api/device-hub/enabled");
      if (!response.ok) return;
      const { enabled } = await response.json();
      devicesTab.classList.toggle("hidden", !enabled);
    } catch {
      // Leave the tab hidden - same fail-closed-on-a-feature-flag reasoning as check() below,
      // which deliberately fails open only for the login overlay.
    }
  }

  async function check() {
    try {
      const response = await fetch("/api/auth/status");
      const payload = await response.json();
      logoutBtn.classList.toggle("hidden", !payload.enabled);
      // Meaningless when login isn't required - there's no real account to show or manage,
      // since every request is treated as a synthetic admin (see AuthGateMiddleware).
      accountsTab.classList.toggle("hidden", !payload.enabled);
      // /api/settings is Admin-only server-side once login is required - hide the tab for a
      // signed-in Viewer rather than let them click through to a raw 403.
      settingsTab.classList.toggle("hidden", payload.enabled && payload.role !== "Admin");
      // The Device Hub tab only exists when it's switched on in Settings. Unlike the two above
      // this isn't a role decision - a Viewer gets the tab too, they just can't add or edit
      // devices there - so it needs its own (non-admin) endpoint to ask.
      if (payload.authenticated || !payload.enabled) await checkDeviceHub();
      if (payload.authenticated) return;

      if (payload.enabled && !payload.configured) useFirstAdminMode();
      overlay.classList.remove("hidden");
      usernameInput.focus();
    } catch {
      // If the check itself fails, don't lock someone out of a dashboard that might not even
      // have login enabled - fail open on the client, same as license.js does. The real
      // security boundary is server-side (AuthGateMiddleware), not this overlay.
    }
  }

  return { check };
})();
