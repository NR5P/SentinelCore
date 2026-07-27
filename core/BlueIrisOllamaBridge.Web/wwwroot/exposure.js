// Shows a banner once something has actually reached this dashboard from outside the local
// network. Evidence-based on purpose: both services bind to all interfaces by design, so there's
// no configuration that says "LAN-only" - an off-LAN request is the first real signal, and until
// one arrives this stays silent rather than nagging every LAN user forever.
window.GateWatchExposure = (function () {
  async function check() {
    const banner = document.getElementById("exposure-banner");
    try {
      const response = await fetch("/api/exposure/status");
      if (!response.ok) return;
      const payload = await response.json();

      if (!payload.externalRequestSeen) {
        banner.classList.add("hidden");
        return;
      }

      banner.textContent = payload.loginEnabled
        ? `reached from ${payload.lastExternalAddress}, outside your network - login is on, but this is plain HTTP. Put it behind HTTPS if that was intended.`
        : `reached from ${payload.lastExternalAddress}, outside your network, with no login set - anyone who can reach this address can view your cameras and change settings.`;
      banner.classList.toggle("exposure-banner-severe", !payload.loginEnabled);
      banner.classList.remove("hidden");
    } catch {
      // Same rule as the license check: if the check itself fails, say nothing rather than
      // show a scary message we can't stand behind.
    }
  }

  return { check };
})();
