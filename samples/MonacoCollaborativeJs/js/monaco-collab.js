// monaco-collab.js
// Wires a Monaco editor instance to an OpStream document over WebSocket and runs
// the standard client-side OT state machine (inflight + buffer) so local typing
// and remote edits converge with the authoritative server.

import { WebSocketOpStreamClient } from "./WebSocketOpStreamClient.js";
import {
  apply, transform, compose, isNoOp, transformOffset, fromMonacoChanges,
} from "./ot-text.js";

const REMOTE_CURSOR_COLORS = ["#e91e63", "#3f51b5", "#009688", "#ff9800", "#9c27b0", "#795548"];

// ── base64 <-> bytes <-> JSON helpers ────────────────────────────────────────
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
    if (typeof payload === 'string') {
        return JSON.parse(new TextDecoder().decode(base64ToBytes(payload)));
    }
    return payload; // Assume already an object if not string
}
function decodeSnapshot(b64) {
  if (!b64) return { content: "" };
  const text = new TextDecoder().decode(base64ToBytes(b64));
  if (!text) return { content: "" };
  try { return JSON.parse(text); } catch { return { content: text }; }
}

/**
 * @param {monaco.editor.IStandaloneCodeEditor} editor
 * @param {object} opts
 * @param {string}   opts.url           ws(s):// URL.
 * @param {string}   opts.documentId
 * @param {object}  [opts.presence]     { name, color } to broadcast remote cursors.
 * @param {function}[opts.onStatus]     (status:string) => void
 * @returns {{ dispose: () => void }}
 */
export function attachCollab(editor, opts) {
  const documentId = opts.documentId;
  const presence = opts.presence || null;

  // ── OT state machine ───────────────────────────────────────────────────────
  let revision = 0;
  let inflight = null;   // sent, awaiting OpResponse
  let buffer = null;     // local edits made while inflight is outstanding
  let applyingRemote = false;
  let disposed = false;

  const client = new WebSocketOpStreamClient(opts.url);

  if (opts.onStatus) {
      // WebSocketOpStreamClient doesn't have a status callback in the constructor,
      // but we can wrap the onDisconnected event.
      client.onDisconnected = (err) => {
          if (!disposed) opts.onStatus("reconnecting");
          console.warn("WebSocket disconnected", err);
      };
      opts.onStatus("connecting");
  }

  // On reconnect, re-join and re-seed from the authoritative server snapshot. This
  // discards any edits made while offline (server is the source of truth) but
  // guarantees convergence after a dropped connection.
  client.onReconnected = async () => {
      if (disposed) return;
      try {
          await joinAndSeed();
      } catch (e) {
          if (opts.onStatus) opts.onStatus("error: " + (e && e.message));
      }
  };

  // ── Local edits → ops ───────────────────────────────────────────────────────
  const changeSub = editor.onDidChangeModelContent((e) => {
    if (applyingRemote || disposed) return;
    const op = fromMonacoChanges(e.changes);
    if (isNoOp(op)) return;

    if (inflight === null) {
      inflight = op;
      flushInflight();
    } else {
      buffer = buffer ? compose(buffer, op) : op;
    }
  });

  async function flushInflight() {
    if (inflight === null || disposed) return;
    const toSend = inflight;
    try {
        const res = await client.sendOpAsync(documentId, encodeOp(toSend), revision);
        if (disposed) return;
        
        if (!res || !res.success) {
            revision = res && res.newRevision ? res.newRevision : revision;
        } else {
            revision = res.newRevision;
        }

        if (buffer !== null) {
            inflight = buffer;
            buffer = null;
            flushInflight();
        } else {
            inflight = null;
        }
    } catch (err) {
        console.error("Failed to send op", err);
    }
  }

  // ── Remote ops → editor ─────────────────────────────────────────────────────
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
    if (!isNoOp(toApply)) applyRemoteToModel(toApply);
  };

  function applyRemoteToModel(op) {
    const m = editor.getModel();
    if (!m) return;

    const edits = [];
    let offset = 0;
    for (const c of op.components) {
      if (c.type === "retain") {
        offset += c.count;
      } else if (c.type === "delete") {
        const start = m.getPositionAt(offset);
        const end = m.getPositionAt(offset + c.count);
        edits.push({ start: offset, range: monaco.Range.fromPositions(start, end), text: "" });
        offset += c.count;
      } else { // insert
        const p = m.getPositionAt(offset);
        edits.push({ start: offset, range: monaco.Range.fromPositions(p, p), text: c.text });
      }
    }
    edits.sort((a, b) => b.start - a.start);

    const sel = editor.getSelection();
    const caretOffset = sel ? m.getOffsetAt(sel.getPosition()) : null;

    applyingRemote = true;
    try {
      m.applyEdits(edits.map(e => ({ range: e.range, text: e.text })));
    } finally {
      applyingRemote = false;
    }

    if (caretOffset !== null) {
      const newCaret = transformOffset(caretOffset, op);
      const pos = m.getPositionAt(newCaret);
      editor.setPosition(pos);
    }

    if (presence) broadcastSelection();
  }

  // ── Presence / remote cursors ───────────────────────────────────────────────
  const remoteDecorations = new Map();
  const remoteWidgets = new Map();     // peerId → content widget instance
  const peerColors = new Map();
  const peerNames = new Map();
  const peerTypingTimers = new Map();
  let colorIdx = 0;
  let awarenessTimer = null;

  function colorFor(peerId, declared) {
    if (declared) return declared;
    if (!peerColors.has(peerId)) {
      peerColors.set(peerId, REMOTE_CURSOR_COLORS[colorIdx++ % REMOTE_CURSOR_COLORS.length]);
    }
    return peerColors.get(peerId);
  }

  function broadcastSelection() {
    const m = editor.getModel();
    const sel = editor.getSelection();
    if (!m || !sel) return;
    client.sendAwarenessAsync(documentId, {
      name: presence.name || "Anonymous",
      color: presence.color || null,
      anchor: m.getOffsetAt(sel.getStartPosition()),
      head: m.getOffsetAt(sel.getEndPosition()),
    });
  }

  const cursorSub = presence
    ? editor.onDidChangeCursorSelection(() => {
        if (applyingRemote) return;
        clearTimeout(awarenessTimer);
        awarenessTimer = setTimeout(broadcastSelection, 50);
      })
    : { dispose() {} };

  // ── Content Widget for peer name label ──────────────────────────────────────
  // Monaco Content Widgets render in an overlay layer that is NOT clipped by
  // the line's overflow:hidden, so the label is always visible above the cursor.
  function createOrUpdatePeerWidget(peerId, position, color, peerName) {
    const widgetId = `peer-label-${cssId(peerId)}`;
    let existing = remoteWidgets.get(peerId);

    if (existing) {
      // Update position and content
      existing._position = position;
      existing._domNode.textContent = peerName;
      existing._domNode.style.background = color;
      editor.layoutContentWidget(existing);
    } else {
      // Create new widget
      const domNode = document.createElement("div");
      domNode.textContent = peerName;
      domNode.style.cssText = `
        font-size: 0.7rem;
        font-family: 'Inter', system-ui, sans-serif;
        font-weight: 600;
        padding: 1px 6px;
        border-radius: 3px 3px 3px 0;
        white-space: nowrap;
        line-height: 1.4;
        background: ${color};
        color: #fff;
        box-shadow: 0 1px 4px rgba(0,0,0,0.35);
        pointer-events: none;
        transition: opacity 0.3s;
        opacity: 1;
      `;

      const widget = {
        _domNode: domNode,
        _position: position,
        getId: () => widgetId,
        getDomNode: () => domNode,
        getPosition: () => ({
          position: widget._position,
          preference: [
            monaco.editor.ContentWidgetPositionPreference.ABOVE,
          ],
        }),
      };

      remoteWidgets.set(peerId, widget);
      editor.addContentWidget(widget);
    }
  }

  function removePeerWidget(peerId) {
    const widget = remoteWidgets.get(peerId);
    if (widget) {
      editor.removeContentWidget(widget);
      remoteWidgets.delete(peerId);
    }
  }

  function showPeerWidget(peerId, visible) {
    const widget = remoteWidgets.get(peerId);
    if (widget) {
      widget._domNode.style.opacity = visible ? "1" : "0";
    }
  }

  function renderRemote(peerId, data) {
    const m = editor.getModel();
    if (!m || !data) return;
    const color = colorFor(peerId, data.color);
    const peerName = data.name || "Anonymous";

    // Track peer name
    peerNames.set(peerId, peerName);

    // Notify the online users UI in index.html
    if (window._collabCallbacks && window._collabCallbacks.onAwareness) {
      window._collabCallbacks.onAwareness(peerId, { name: peerName, color });
    }

    const a = m.getPositionAt(Math.min(data.anchor ?? 0, m.getValueLength?.() ?? Number.MAX_SAFE_INTEGER));
    const h = m.getPositionAt(data.head ?? data.anchor ?? 0);
    const id = cssId(peerId);

    ensurePeerStyle(peerId, color);

    const hasSelection = !a.equals(h);
    const decos = [];

    if (hasSelection) {
      // Selection range with colored background
      decos.push({
        range: monaco.Range.fromPositions(a, h),
        options: {
          className: `collab-remote-selection collab-peer-${id}`,
          beforeContentClassName: `collab-remote-caret collab-peer-${id}`,
          stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
        },
      });
    } else {
      // Just a caret
      decos.push({
        range: monaco.Range.fromPositions(a, h),
        options: {
          beforeContentClassName: `collab-remote-caret collab-peer-${id}`,
          stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
        },
      });
    }

    const prev = remoteDecorations.get(peerId) || [];
    remoteDecorations.set(peerId, editor.deltaDecorations(prev, decos));

    // Name label via Content Widget (renders in unclipped overlay layer)
    createOrUpdatePeerWidget(peerId, h, color, peerName);

    // Auto-hide the label after 4 seconds of inactivity
    clearTimeout(peerTypingTimers.get(peerId));
    showPeerWidget(peerId, true);
    peerTypingTimers.set(peerId, setTimeout(() => {
      showPeerWidget(peerId, false);
    }, 4000));
  }

  function cssId(peerId) { return peerId.replace(/[^a-zA-Z0-9_-]/g, ""); }

  const injectedStyles = new Set();
  function ensurePeerStyle(peerId, color) {
    const id = cssId(peerId);
    if (injectedStyles.has(id)) return;
    injectedStyles.add(id);

    const style = document.createElement("style");
    style.id = `collab-style-${id}`;

    style.textContent =
      // Selection background — use outline instead of border (doesn't affect layout)
      `.collab-peer-${id}.collab-remote-selection {
        background: ${hexToRgba(color, 0.22)};
        outline: 1.5px solid ${hexToRgba(color, 0.55)};
        outline-offset: -1px;
        border-radius: 2px;
      }` +
      // Caret line
      `.collab-peer-${id}.collab-remote-caret {
        border-left: 2px solid ${color};
        margin-left: -1px;
      }`;
    document.head.appendChild(style);
  }

  function hexToRgba(hex, alpha) {
    const h = hex.replace("#", "");
    const n = parseInt(h.length === 3 ? h.split("").map(c => c + c).join("") : h, 16);
    return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
  }

  client.onReceiveAwareness = (list) => {
    for (const s of list) {
      // The server serializes AwarenessState as { peerId, data, lastUpdated } (camelCase).
      // `data` is the presence object the peer broadcast ({ name, color, anchor, head }).
      // Tolerate a JSON string too in case a peer sends pre-stringified data.
      const raw = s.data ?? s.dataJson;
      try { renderRemote(s.peerId, typeof raw === "string" ? JSON.parse(raw) : raw); }
      catch { /* ignore malformed presence */ }
    }
  };
  client.onPeerDisconnected = (peerId) => {
    // Remove decorations
    const prev = remoteDecorations.get(peerId);
    if (prev) { editor.deltaDecorations(prev, []); remoteDecorations.delete(peerId); }

    // Remove content widget
    removePeerWidget(peerId);

    peerNames.delete(peerId);
    clearTimeout(peerTypingTimers.get(peerId));
    peerTypingTimers.delete(peerId);

    // Remove the injected style
    const id = cssId(peerId);
    const existingStyle = document.getElementById(`collab-style-${id}`);
    if (existingStyle) existingStyle.remove();
    injectedStyles.delete(id);

    // Notify the online users UI
    if (window._collabCallbacks && window._collabCallbacks.onPeerDisconnected) {
      window._collabCallbacks.onPeerDisconnected(peerId);
    }
  };

  // ── Join / resync ───────────────────────────────────────────────────────────
  async function joinAndSeed() {
    const res = await client.connectAndJoinAsync(documentId, "text");
    const snapshot = decodeSnapshot(res.snapshot);
    const text = snapshot.content || "";

    applyingRemote = true;
    try {
      const m = editor.getModel();
      m.setEOL(monaco.editor.EndOfLineSequence.LF);
      if (m.getValue() !== text) m.setValue(text);
    } finally {
      applyingRemote = false;
    }

    revision = res.revision;
    inflight = null;
    buffer = null;
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
    // Exposed so the host can inspect connection state (and for tests).
    client,
    dispose() {
      if (disposed) return;
      disposed = true;
      clearTimeout(awarenessTimer);
      changeSub.dispose();
      cursorSub.dispose();
      for (const ids of remoteDecorations.values()) editor.deltaDecorations(ids, []);
      remoteDecorations.clear();
      for (const widget of remoteWidgets.values()) editor.removeContentWidget(widget);
      remoteWidgets.clear();
      for (const timer of peerTypingTimers.values()) clearTimeout(timer);
      peerTypingTimers.clear();
      client.disposeAsync();
    },
  };
}
