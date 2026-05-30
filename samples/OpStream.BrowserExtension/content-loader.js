// content-loader.js  (classic content script — runs in the isolated world)
//
// Injects a small floating panel into every page. The user picks a <textarea>
// or text <input> by clicking it, then the field is wired to an OpStream
// document so the SAME field on another browser/tab stays in sync.
//
// Content scripts can't use top-level `import`, so the OT adapter (an ES module)
// is loaded dynamically from web_accessible_resources via chrome.runtime.getURL.

(function () {
  "use strict";

  // Guard against double injection (SPA navigations / re-injection).
  if (window.__opstreamCollabInjected) return;
  window.__opstreamCollabInjected = true;

  const DEFAULTS = {
    url: "ws://localhost:50109/collab-ws",
    name: "User-" + Math.floor(Math.random() * 1000),
  };

  let attachment = null;       // { dispose() } from the active adapter
  let pickMode = false;
  let pickedField = null;
  let pickedKind = null;       // "textarea" | "contenteditable"
  let hoverEl = null;

  // Resolve the element we actually attach to (the contenteditable root, or the
  // field itself), so hovering/clicking a text node inside a rich editor selects
  // the editing root rather than a child span.
  function resolveField(el) {
    const kind = fieldKind(el);
    if (kind === "contenteditable") {
      return el.closest('[contenteditable=""],[contenteditable="true"]');
    }
    return kind ? el : null;
  }

  // ── Panel UI ────────────────────────────────────────────────────────────
  const panel = document.createElement("div");
  panel.id = "opstream-collab-panel";
  panel.innerHTML = `
    <div class="ocp-header">
      <span class="ocp-dot ocp-dot--idle"></span>
      <strong>OpStream Collab</strong>
      <button class="ocp-x" title="Hide">×</button>
    </div>
    <div class="ocp-body">
      <label>Server
        <input class="ocp-url" type="text" value="${DEFAULTS.url}" spellcheck="false" />
      </label>
      <label>Document ID
        <input class="ocp-doc" type="text" placeholder="(auto from URL)" spellcheck="false" />
      </label>
      <label>Name
        <input class="ocp-name" type="text" value="${DEFAULTS.name}" spellcheck="false" />
      </label>
      <label class="ocp-check">
        <input class="ocp-sw" type="checkbox" />
        Route via service worker (bypass page CSP)
      </label>
      <button class="ocp-pick">1 · Pick a text field</button>
      <button class="ocp-go" disabled>2 · Make collaborative</button>
      <button class="ocp-stop" hidden>Disconnect</button>
      <div class="ocp-status">Idle — pick a &lt;textarea&gt; or text input.</div>
    </div>`;
  document.documentElement.appendChild(panel);

  const $ = (sel) => panel.querySelector(sel);
  const dot = $(".ocp-dot");
  const statusEl = $(".ocp-status");
  const goBtn = $(".ocp-go");
  const pickBtn = $(".ocp-pick");
  const stopBtn = $(".ocp-stop");

  function setStatus(text, kind) {
    statusEl.textContent = text;
    dot.className = "ocp-dot ocp-dot--" + (kind || "idle");
  }

  $(".ocp-x").addEventListener("click", () => { panel.style.display = "none"; });

  // ── Field picking ─────────────────────────────────────────────────────────
  function fieldKind(el) {
    if (!el) return null;
    const tag = el.tagName;
    if (tag === "TEXTAREA") return "textarea";
    if (tag === "INPUT") {
      const t = (el.type || "text").toLowerCase();
      return ["text", "search", "url", "email", "tel", ""].includes(t) ? "textarea" : null;
    }
    // contenteditable — climb to the editing root (the closest editable ancestor).
    const editable = el.closest('[contenteditable=""],[contenteditable="true"]');
    if (editable) return "contenteditable";
    return null;
  }
  function isEditableField(el) { return fieldKind(el) !== null; }

  function onHoverMove(e) {
    if (!pickMode) return;
    const el = e.target;
    if (el === hoverEl) return;
    const resolved = resolveField(el);
    if (hoverEl) hoverEl.classList.remove("ocp-target");
    if (resolved) {
      hoverEl = resolved;
      hoverEl.classList.add("ocp-target");
    } else {
      hoverEl = null;
    }
  }

  function onPickClick(e) {
    if (!pickMode) return;
    if (panel.contains(e.target)) return; // ignore clicks on our own panel
    const resolved = resolveField(e.target);
    if (!resolved) return;
    e.preventDefault();
    e.stopPropagation();
    selectField(resolved);
  }

  function selectField(el) {
    if (pickedField) pickedField.classList.remove("ocp-selected");
    pickedField = el;
    pickedKind = fieldKind(el);
    pickedField.classList.add("ocp-selected");
    stopPicking();
    goBtn.disabled = false;
    setStatus(`Field selected (${pickedKind}). Click “Make collaborative”.`, "idle");
  }

  function startPicking() {
    pickMode = true;
    document.addEventListener("mousemove", onHoverMove, true);
    document.addEventListener("click", onPickClick, true);
    setStatus("Click any text field on the page…", "picking");
    pickBtn.textContent = "Cancel picking";
  }

  function stopPicking() {
    pickMode = false;
    document.removeEventListener("mousemove", onHoverMove, true);
    document.removeEventListener("click", onPickClick, true);
    if (hoverEl) { hoverEl.classList.remove("ocp-target"); hoverEl = null; }
    pickBtn.textContent = "1 · Pick a text field";
  }

  pickBtn.addEventListener("click", () => {
    if (attachment) return;
    if (pickMode) stopPicking();
    else startPicking();
  });

  // ── Document ID: stable per page+field unless the user overrides it ─────────
  function autoDocId(field) {
    const base = location.origin + location.pathname;
    const fields = [...document.querySelectorAll(
      'textarea, input, [contenteditable=""], [contenteditable="true"]')];
    const idx = Math.max(0, fields.indexOf(field));
    return "ext:" + base + "#field" + idx;
  }

  // ── Wire it up ──────────────────────────────────────────────────────────────
  goBtn.addEventListener("click", async () => {
    if (!pickedField || attachment) return;
    const url = $(".ocp-url").value.trim();
    const name = $(".ocp-name").value.trim() || "Anonymous";
    const documentId = $(".ocp-doc").value.trim() || autoDocId(pickedField);
    $(".ocp-doc").value = documentId;

    const useServiceWorker = $(".ocp-sw").checked;

    setStatus("Connecting…", "picking");
    goBtn.disabled = true;

    try {
      // When routing via the service worker, swap the transport for the Port
      // proxy. The OT adapters don't care which client they get.
      let createClient;
      if (useServiceWorker) {
        const swMod = await import(chrome.runtime.getURL("collab/sw-port-client.js"));
        createClient = (u) => new swMod.SwPortOpStreamClient(u);
      }

      // Pick the adapter that matches the field type.
      let attach;
      if (pickedKind === "contenteditable") {
        const mod = await import(chrome.runtime.getURL("collab/contenteditable-collab.js"));
        attach = mod.attachContentEditableCollab;
      } else {
        const mod = await import(chrome.runtime.getURL("collab/textarea-collab.js"));
        attach = mod.attachTextareaCollab;
      }

      attachment = attach(pickedField, {
        url,
        documentId,
        createClient,
        presence: { name, color: null },
        onStatus: (s) => {
          if (s === "open") setStatus("Connected · live", "open");
          else if (s === "connecting") setStatus("Connecting…", "picking");
          else if (s === "closed") setStatus("Disconnected", "error");
          else if (s.startsWith("error")) setStatus(s, "error");
        },
        onPeers: (ids) => {
          const n = ids.length;
          if (dot.className.includes("open")) {
            setStatus(`Connected · live · ${n} peer${n === 1 ? "" : "s"}`, "open");
          }
        },
      });
      stopBtn.hidden = false;
      pickBtn.disabled = true;
    } catch (err) {
      console.error("[OpStream] attach failed", err);
      setStatus("Failed: " + (err && err.message), "error");
      goBtn.disabled = false;
    }
  });

  stopBtn.addEventListener("click", () => {
    if (attachment) { attachment.dispose(); attachment = null; }
    if (pickedField) pickedField.classList.remove("ocp-selected");
    stopBtn.hidden = true;
    pickBtn.disabled = false;
    goBtn.disabled = pickedField ? false : true;
    setStatus("Disconnected.", "idle");
  });
})();
