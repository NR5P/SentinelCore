window.GateWatchAuth = (function () {
  const overlay = document.getElementById("login-gate");
  const usernameInput = document.getElementById("login-username");
  const passwordInput = document.getElementById("login-password");
  const submitBtn = document.getElementById("login-submit-btn");
  const errorsEl = document.getElementById("login-errors");
  const logoutBtn = document.getElementById("logout-btn");

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
    submitBtn.disabled = true;
    try {
      const response = await fetch("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username: usernameInput.value.trim(), password: passwordInput.value })
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        showErrors([response.status === 429 ? "Too many attempts - wait a few minutes and try again." : (payload.error || "Sign-in failed.")]);
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
  passwordInput.addEventListener("keydown", (e) => { if (e.key === "Enter") submit(); });
  usernameInput.addEventListener("keydown", (e) => { if (e.key === "Enter") passwordInput.focus(); });

  logoutBtn.addEventListener("click", async () => {
    await fetch("/api/auth/logout", { method: "POST" });
    window.location.reload();
  });

  async function check() {
    try {
      const response = await fetch("/api/auth/status");
      const payload = await response.json();
      logoutBtn.classList.toggle("hidden", !payload.enabled);
      if (payload.authenticated) return;

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
