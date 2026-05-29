// textarea-collab.js
// Wires a plain <textarea> (or <input type=text>) on ANY web page to an OpStream
// document over WebSocket, running the same client-side OT state machine
// (inflight + buffer) as the Monaco adapter so local typing and remote edits
// converge against the authoritative server.
//
// This is the editor-agnostic "simple case" adapter: it treats the field value
// as a flat string and turns each `input` event into a single contiguous TextOp
// via a prefix/suffix diff. Rich editors (contenteditable, Google Docs, canvas)
// need their own adapter — see radzen-collab-adapter.js for the contenteditable
// pattern.

import { WebSocketOpStreamClient } from "./WebSocketOpStreamClient.js";
import {
  apply, transform, compose, isNoOp, transformOffset, diffToOp,
} from "./ot-text.js";

// ── base64 <-> bytes <-> JSON helpers (match monaco-collab.js wire format) ───
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

/**
 * Attach OpStream collaboration to a textarea/input element.
 * @param {HTMLTextAreaElement|HTMLInputElement} field
 * @param {object} opts
 * @param {string}  opts.url          ws(s):// URL (e.g. ws://localhost:50109/collab-ws)
 * @param {string}  opts.documentId
 * @param {object} [opts.presence]    { name, color } broadcast to peers
 * @param {function}[opts.onStatus]   (status:string) => void
 * @param {function}[opts.onPeers]    (peerIds:string[]) => void
 * @param {function}[opts.createClient] (url) => client. Swap the transport (e.g.
 *        a service-worker proxy) without touching the OT logic. Defaults to a
 *        direct WebSocketOpStreamClient.
 * @returns {{ dispose: () => void }}
 */
export function attachTextareaCollab(field, opts) {
  const documentId = opts.documentId;
  const presence = opts.presence || null;

  // ── OT state machine ────────────────────────────────────────────────────
  let revision = 0;
  let inflight = null;   // sent op awaiting OpResponse
  let buffer = null;     // local edits made while inflight is outstanding
  let applyingRemote = false;
  let disposed = false;
  let lastValue = field.value;

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

  // ── Local edits → ops ─────────────────────────────────────────────────────
  function onLocalInput() {
    if (applyingRemote || disposed) return;
    const newValue = field.value;
    if (newValue === lastValue) return;
    const op = diffToOp(lastValue, newValue);
    lastValue = newValue;
    if (isNoOp(op)) return;

    if (inflight === null) {
      inflight = op;
      flushInflight();
    } else {
      buffer = buffer ? compose(buffer, op) : op;
    }
  }
  field.addEventListener("input", onLocalInput);

  async function flushInflight() {
    if (inflight === null || disposed) return;
    const toSend = inflight;
    try {
      const res = await client.sendOpAsync(documentId, encodeOp(toSend), revision);
      if (disposed) return;
      revision = res && res.newRevision ? res.newRevision : revision;

      if (buffer !== null) {
        inflight = buffer;
        buffer = null;
        flushInflight();
      } else {
        inflight = null;
      }
    } catch (err) {
      console.error("[OpStream] Failed to send op", err);
    }
  }

  // ── Remote ops → field ────────────────────────────────────────────────────
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
    if (!isNoOp(toApply)) applyRemoteToField(toApply);
  };

  function applyRemoteToField(op) {
    const oldVal = field.value;
    const newVal = apply(oldVal, op);

    const selStart = field.selectionStart ?? newVal.length;
    const selEnd = field.selectionEnd ?? selStart;
    // Some browsers/elements don't expose a usable selection while unfocused.
    const hadSelection = document.activeElement === field;

    applyingRemote = true;
    try {
      field.value = newVal;
      lastValue = newVal;
    } finally {
      applyingRemote = false;
    }

    if (hadSelection) {
      try {
        field.selectionStart = transformOffset(selStart, op);
        field.selectionEnd = transformOffset(selEnd, op);
      } catch { /* element may not support selection range */ }
    }

    if (presence) broadcastSelection();
  }

  // ── Presence (lightweight: peer set only; no remote caret overlay yet) ─────
  function broadcastSelection() {
    if (!presence) return;
    client.sendAwarenessAsync(documentId, {
      name: presence.name || "Anonymous",
      color: presence.color || null,
      anchor: field.selectionStart ?? 0,
      head: field.selectionEnd ?? field.selectionStart ?? 0,
    });
  }

  let awarenessTimer = null;
  const onSelect = presence
    ? () => {
        if (applyingRemote) return;
        clearTimeout(awarenessTimer);
        awarenessTimer = setTimeout(broadcastSelection, 80);
      }
    : null;
  if (onSelect) {
    field.addEventListener("keyup", onSelect);
    field.addEventListener("click", onSelect);
  }

  client.onReceiveAwareness = (list) => {
    for (const s of list) {
      if (s && s.peerId) peers.add(s.peerId);
    }
    if (opts.onPeers) opts.onPeers([...peers]);
  };
  client.onPeerDisconnected = (peerId) => {
    peers.delete(peerId);
    if (opts.onPeers) opts.onPeers([...peers]);
  };

  // ── Join / seed ────────────────────────────────────────────────────────────
  async function joinAndSeed() {
    const res = await client.connectAndJoinAsync(documentId, "text");
    const snapshot = decodeSnapshot(res.snapshot);
    const text = snapshot.content || "";

    applyingRemote = true;
    try {
      field.value = text;
      lastValue = text;
    } finally {
      applyingRemote = false;
    }

    revision = res.revision;
    inflight = null;
    buffer = null;
    if (Array.isArray(res.currentAwareness)) {
      for (const s of res.currentAwareness) if (s && s.peerId) peers.add(s.peerId);
      if (opts.onPeers) opts.onPeers([...peers]);
    }
    if (opts.onStatus) opts.onStatus("open");
  }

  (async () => {
    try {
      await joinAndSeed();
    } catch (e) {
      if (opts.onStatus) opts.onStatus("error: " + (e && e.message));
    }
  })();

  // ── Teardown ────────────────────────────────────────────────────────────────
  return {
    dispose() {
      if (disposed) return;
      disposed = true;
      clearTimeout(awarenessTimer);
      field.removeEventListener("input", onLocalInput);
      if (onSelect) {
        field.removeEventListener("keyup", onSelect);
        field.removeEventListener("click", onSelect);
      }
      client.disposeAsync();
    },
  };
}
