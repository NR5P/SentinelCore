// Nudges toward the full AI. A new install runs on the fast detector until someone picks a full
// AI (see the setup wizard's vision step), so this says so, shows the Ollama model's download
// while it runs, and speaks up if Ollama is supposed to be doing the work but can't. State comes
// from /api/system/full-ai; non-admins get a 403 there and simply see nothing.
window.GateWatchFullAi = (function () {
  const DISMISS_KEY = "fullAiNudgeDismissedUntil";
  const DISMISS_DAYS = 30;
  const DOWNLOAD_POLL_MS = 5000;
  let pollTimer = null;

  function dismissedUntil() {
    try {
      return Number(localStorage.getItem(DISMISS_KEY)) || 0;
    } catch {
      return 0;
    }
  }

  function render(banner, status) {
    banner.innerHTML = "";
    banner.classList.remove("full-ai-banner-bad");
    const text = document.createElement("span");
    banner.appendChild(text);

    switch (status.state) {
      case "quick": {
        // The only one that can be dismissed: it is a suggestion, the others are things in motion
        // or things that are wrong. Comes back after a month so it isn't forgotten for good.
        if (Date.now() < dismissedUntil()) return false;
        text.textContent = "alerts are on quick detection - they say \"person\" or \"vehicle\", not what's happening.";
        banner.appendChild(button("use the full AI", useOllama));
        banner.appendChild(button("not now", () => {
          try {
            localStorage.setItem(DISMISS_KEY, String(Date.now() + DISMISS_DAYS * 24 * 60 * 60 * 1000));
          } catch {
            // Storage blocked: dismissing just lasts until the next page load.
          }
          banner.classList.add("hidden");
        }));
        return true;
      }
      case "downloading":
        text.textContent = `downloading the full AI model (${status.model}) - ${status.percent}%. ` +
          "alerts use quick detection until it finishes.";
        return true;
      case "download-failed":
        text.textContent = `the full AI model (${status.model}) didn't download: ${status.error}. ` +
          "retrying every 10 minutes - alerts use quick detection meanwhile.";
        banner.classList.add("full-ai-banner-bad");
        return true;
      case "ollama-unreachable":
        text.textContent = "Ollama isn't running, so alerts can't be analyzed. start it, or pick another option in SETTINGS -> VISION ROUTING.";
        banner.appendChild(button("open settings", openVisionSettings));
        banner.classList.add("full-ai-banner-bad");
        return true;
      default:
        return false;
    }
  }

  function button(label, onClick) {
    const el = document.createElement("button");
    el.type = "button";
    el.className = "full-ai-banner-btn";
    el.textContent = label;
    el.addEventListener("click", onClick);
    return el;
  }

  async function useOllama() {
    if (!confirm("Switch to the full AI on this machine (Ollama)?\n\nIt downloads a 4.3 GB model in the " +
      "background, and alerts keep using quick detection until it finishes. Without a graphics card each " +
      "alert can take a while to analyze.")) return;
    try {
      await fetch("/api/system/full-ai/use-ollama", { method: "POST" });
    } catch {
      // Falls through to the re-check, which shows whatever state actually resulted.
    }
    // The main app picks the change up from appsettings.json a moment later.
    setTimeout(check, 1500);
  }

  function openVisionSettings() {
    document.querySelector('#tabs .tab[data-view="settings"]')?.click();
  }

  async function check() {
    const banner = document.getElementById("full-ai-banner");
    clearTimeout(pollTimer);
    try {
      const response = await fetch("/api/system/full-ai");
      if (!response.ok) {
        banner.classList.add("hidden");
        return;
      }
      const status = await response.json();
      banner.classList.toggle("hidden", !render(banner, status));
      // Poll only while something is moving; a finished download clears the banner on its own.
      if (status.state === "downloading") pollTimer = setTimeout(check, DOWNLOAD_POLL_MS);
    } catch {
      // Same rule as the licence and exposure banners: if the check fails, say nothing.
    }
  }

  return { check };
})();
