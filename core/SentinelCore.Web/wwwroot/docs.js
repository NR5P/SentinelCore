const docsListEl = document.getElementById("docs-list");
const docsFrameEl = document.getElementById("docs-frame");

let docsLoaded = false;

function selectDoc(slug) {
  docsFrameEl.src = `/api/docs/${encodeURIComponent(slug)}/pdf`;
  for (const btn of docsListEl.querySelectorAll(".docs-list-item")) {
    btn.classList.toggle("active", btn.dataset.slug === slug);
  }
}

async function loadDocs() {
  try {
    const response = await fetch("/api/docs");
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const docs = await response.json();
    // Set from the result, not ahead of it: a first load that failed used to be remembered as a
    // load that had happened, so every later visit skipped the fetch and the tab stayed on
    // "could not load docs" until the page was reloaded.
    docsLoaded = true;

    docsListEl.innerHTML = "";
    if (docs.length === 0) {
      docsListEl.innerHTML = `<div class="empty-state">no docs found</div>`;
      return;
    }

    for (const doc of docs) {
      const btn = document.createElement("button");
      btn.className = "docs-list-item";
      btn.dataset.slug = doc.slug;
      btn.textContent = doc.title;
      btn.addEventListener("click", () => selectDoc(doc.slug));
      docsListEl.appendChild(btn);
    }

    selectDoc(docs[0].slug);
  } catch (err) {
    docsListEl.innerHTML = `<div class="empty-state">could not load docs</div>`;
    console.error("Failed to load docs", err);
  }
}

window.GateWatchDocs = {
  load() {
    if (docsLoaded) return;
    loadDocs();
  }
};
