// contenteditable-collab.js
// Wires a rich `contenteditable` element (Medium, Quill, a plain div, …) to an
// OpStream document. The document string IS the element's innerHTML: each DOM
// mutation is diffed into a contiguous TextOp, and remote ops are applied by
// swapping innerHTML and restoring the caret by visible-text offset.
//
// It combines:
//   • the OT state machine + wire format from textarea-collab.js, and
//   • the contenteditable change-detection + caret mapping from the Radzen
//     adapter (samples/OpStream.CollabHtmlEditor/wwwroot/js/radzen-collab-adapter.js).
//
// CAVEAT: editors with their own internal model/undo/autosave (Medium!) may
// fight a wholesale innerHTML swap — expect caret jumps and possible conflicts
// with the host editor's state. This is the "rich editor needs its own adapter"
// reality; treat this as a does-it-sync-at-all probe, not production-grade.

import { WebSocketOpStreamClient } from "./WebSocketOpStreamClient.js";
import {
  apply, transform, compose, isNoOp, diffToOp,
} from "./ot-text.js";

// ── wire helpers (identical to textarea-collab.js) ───────────────────────────
function base64ToBytes(b64) {
  const bin = atob(b64);
  const arr = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
  return arr;
}
function encodeOp(op) {
  const bytes = new TextEncoder().encode(JSON.stringify(op));
  let bin = "";
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin);
}
function decodeOp(payload) {
  if (typeof payload === "string") {
    return JSON.parse(new TextDecoder().decode(base64ToBytes(payload)));
  }
  return payload;
}
function decodeSnapshot(b64) {
  if (!b64) return { content: "" };
  const text = new TextDecoder().decode(base64ToBytes(b64));
  if (!text) return { content: "" };
  try { return JSON.parse(text); } catch { return { content: text }; }
}

// ── caret <-> visible-text offset (ported from the Radzen adapter) ───────────
function textOffsetBefore(root, target) {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
  let offset = 0;
  while (walker.nextNode()) {
    const node = walker.currentNode;
    if (node === target) return offset;
    if (node.compareDocumentPosition(target) & Node.DOCUMENT_POSITION_PRECEDING) return offset;
    offset += node.textContent.length;
  }
  return offset;
}
function getCaretOffset(root) {
  const sel = window.getSelection();
  if (!sel || sel.rangeCount === 0) return null;
  const range = sel.getRangeAt(0);
  if (!root.contains(range.startContainer)) return null;
  if (range.startContainer.nodeType === Node.ELEMENT_NODE) {
    const container = range.startContainer;
    let offset = 0;
    for (let i = 0; i < range.startOffset && i < container.childNodes.length; i++) {
      offset += container.childNodes[i].textContent.length;
    }
    return textOffsetBefore(root, container) + offset;
  }
  return textOffsetBefore(root, range.startContainer) + range.startOffset;
}
function setCaretOffset(root, charOffset) {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
  let remaining = Math.max(0, charOffset);
  let target = null, targetOff = 0;
  while (walker.nextNode()) {
    const node = walker.currentNode;
    const len = node.textContent.length;
    if (remaining <= len) { target = node; targetOff = remaining; break; }
    remaining -= len;
  }
  const sel = window.getSelection();
  const range = document.createRange();
  if (target) { range.setStart(target, targetOff); range.collapse(true); }
  else { range.selectNodeContents(root); range.collapse(false); }
  sel.removeAllRanges();
  sel.addRange(range);
}
// Best-effort: shift a visible-text caret offset by ops expressed in HTML space.
// Approximate (same trade-off the Radzen adapter accepts) but keeps the caret
// roughly stable under remote edits.
function transformCaret(cursor, op) {
  if (!op) return cursor;
  let pos = 0, out = cursor;
  for (const c of op.components) {
    if (c.type === "retain") pos += c.count;
    else if (c.type === "insert") { if (pos <= out) out += c.text.length; }
    else { if (pos < out) out -= Math.min(c.count, out - pos); pos += c.count; }
  }
  return Math.max(0, out);
}

/**
 * @param {HTMLElement} el  a contenteditable element
 * @param {object} opts     same shape as attachTextareaCollab
 */
export function attachContentEditableCollab(el, opts) {
  const documentId = opts.documentId;
  const presence = opts.presence || null;

  let revision = 0;
  let inflight = null;
  let buffer = null;
  let remoteDepth = 0;        // >0 while applying remote ops (suppress local echo)
  let disposed = false;
  let lastHtml = el.innerHTML;

  const peers = new Set();
  const client = opts.createClient
    ? opts.createClient(opts.url)
    : new WebSocketOpStreamClient(opts.url);

  if (opts.onStatus) {
    client.onDisconnected = (err) => {
      opts.onStatus("closed");
      console.error("[OpStream] WebSocket disconnected", err);
    };
    opts.onStatus("connecting");
  }

  // ── Local DOM changes → ops ────────────────────────────────────────────────
  function onLocalChange() {
    if (remoteDepth > 0 || disposed) return;
    const newHtml = el.innerHTML;
    if (newHtml === lastHtml) return;
    const op = diffToOp(lastHtml, newHtml);
    lastHtml = newHtml;
    if (isNoOp(op)) return;

    if (inflight === null) {
      inflight = op;
      flushInflight();
    } else {
      buffer = buffer ? compose(buffer, op) : op;
    }
  }

  el.addEventListener("input", onLocalChange);
  // Toolbar/formatting and the editor's own programmatic edits don't always fire
  // "input" — a MutationObserver catches those too.
  const observer = new MutationObserver(onLocalChange);
  observer.observe(el, { childList: true, subtree: true, characterData: true, attributes: true });

  async function flushInflight() {
    if (inflight === null || disposed) return;
    const toSend = inflight;
    try {
      const res = await client.sendOpAsync(documentId, encodeOp(toSend), revision);
      if (disposed) return;
      revision = res && res.newRevision ? res.newRevision : revision;
      if (buffer !== null) { inflight = buffer; buffer = null; flushInflight(); }
      else inflight = null;
    } catch (err) {
      console.error("[OpStream] Failed to send op", err);
    }
  }

  // ── Remote ops → element ───────────────────────────────────────────────────
  client.onReceiveOp = (payload, newRevision) => {
    if (disposed) return;
    let toApply = decodeOp(payload);
    if (inflight !== null) {
      const applied = transform(toApply, inflight, "incomingWins");
      inflight = transform(inflight, toApply, "existingWins") || { components: [] };
      toApply = applied;
    }
    if (buffer !== null) {
      const applied = transform(toApply, buffer, "incomingWins");
      buffer = transform(buffer, toApply, "existingWins");
      if (buffer && isNoOp(buffer)) buffer = null;
      toApply = applied;
    }
    revision = newRevision;
    if (!isNoOp(toApply)) applyRemote(toApply);
  };

  function applyRemote(op) {
    const oldHtml = el.innerHTML;
    const newHtml = apply(oldHtml, op);

    const hadCaret = document.activeElement === el || el.contains(document.activeElement);
    const savedOffset = hadCaret ? getCaretOffset(el) : null;

    remoteDepth++;
    try {
      el.innerHTML = newHtml;
      lastHtml = el.innerHTML; // store browser-normalized form
    } finally {
      // MutationObserver callbacks are microtasks; defer the decrement one tick
      // so they observe depth > 0 and don't echo our own remote apply.
      Promise.resolve().then(() => { remoteDepth--; });
    }

    if (savedOffset !== null) {
      try { setCaretOffset(el, transformCaret(savedOffset, op)); } catch { /* node gone */ }
    }
  }

  // ── Presence (lightweight peer count only) ─────────────────────────────────
  client.onReceiveAwareness = (list) => {
    for (const s of list) if (s && s.peerId) peers.add(s.peerId);
    if (opts.onPeers) opts.onPeers([...peers]);
  };
  client.onPeerDisconnected = (peerId) => {
    peers.delete(peerId);
    if (opts.onPeers) opts.onPeers([...peers]);
  };

  // ── Join / seed ─────────────────────────────────────────────────────────────
  async function joinAndSeed() {
    const res = await client.connectAndJoinAsync(documentId, "text");
    const snapshot = decodeSnapshot(res.snapshot);
    const serverHtml = snapshot.content || "";

    revision = res.revision;
    inflight = null;
    buffer = null;

    if (serverHtml && serverHtml !== el.innerHTML) {
      // Server already has content → seed the editor from it.
      remoteDepth++;
      try { el.innerHTML = serverHtml; lastHtml = el.innerHTML; }
      finally { Promise.resolve().then(() => { remoteDepth--; }); }
    } else {
      // Server is empty (fresh doc). Do NOT wipe the host editor — editors like
      // Medium require their own scaffolding (<section>, dividers, paragraph
      // names) and break their autosave if you replace it with "". Instead adopt
      // the editor's current content as the baseline and push it as the first op,
      // so the first peer seeds the document from the live page.
      lastHtml = serverHtml; // typically ""
      const current = el.innerHTML;
      if (current !== lastHtml) {
        const op = diffToOp(lastHtml, current);
        lastHtml = current;
        if (!isNoOp(op)) { inflight = op; flushInflight(); }
      }
    }
    if (Array.isArray(res.currentAwareness)) {
      for (const s of res.currentAwareness) if (s && s.peerId) peers.add(s.peerId);
      if (opts.onPeers) opts.onPeers([...peers]);
    }
    if (opts.onStatus) opts.onStatus("open");
  }

  (async () => {
    try { await joinAndSeed(); }
    catch (e) { if (opts.onStatus) opts.onStatus("error: " + (e && e.message)); }
  })();

  return {
    dispose() {
      if (disposed) return;
      disposed = true;
      observer.disconnect();
      el.removeEventListener("input", onLocalChange);
      client.disposeAsync();
    },
  };
}
