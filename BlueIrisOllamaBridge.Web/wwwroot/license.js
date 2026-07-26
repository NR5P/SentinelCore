window.GateWatchLicense = (function () {
  async function check() {
    const banner = document.getElementById("license-banner");
    try {
      const response = await fetch("/api/license/status");
      const payload = await response.json();
      banner.classList.toggle("hidden", payload.valid);
    } catch {
      // If the check itself fails, say nothing rather than falsely nag - this is visibility,
      // not enforcement, and every feature works regardless either way.
    }
  }

  return { check };
})();
