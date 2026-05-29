// monaco-collab.js
// Wires a Monaco editor instance to an OpStream document over WebSocket and runs
// the standard client-side OT state machine (inflight + buffer) so local typing
// and remote edits converge with the authoritative server.
//
// Tie-break convention (must match the server, which uses
// Transform(incoming, existing, ExistingWins)):
//   - rebase our pending op against a committed remote: transform(local, remote, "existingWins")
//   - apply the remote on top of our pending op:        transform(remote, local, "incomingWins")
// Both make the committed remote op win insert-at-same-offset ties.

import { OpStreamSocket, encodeOp, decodeSnapshot } from "./opstream-ws.js";
import {
  apply, transform, compose, isNoOp, transformOffset, fromMonacoChanges,
} from "./ot-text.js";

const REMOTE_CURSOR_COLORS = ["#e91e63", "#3f51b5", "#009688", "#ff9800", "#9c27b0", "#795548"];

/**
 * @param {monaco.editor.IStandaloneCodeEditor} editor
 * @param {object} opts
 * @param {string}  [opts.url]          ws(s):// URL. Defaults to same-origin /collab-ws.
 * @param {string}   opts.documentId
 * @param {object}  [opts.presence]     { name, color } to broadcast remote cursors.
 * @param {function}[opts.getAuthToken]
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

  const socket = new OpStreamSocket({
    url: opts.url,
    getAuthToken: opts.getAuthToken,
  });
  if (opts.onStatus) socket.onStatus = opts.onStatus;

  let model = editor.getModel();

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

  function flushInflight() {
    if (inflight === null) return;
    const toSend = inflight;
    socket.sendOp(documentId, encodeOp(toSend), revision)
      .then((res) => {
        if (disposed) return;
        if (!res || !res.success) {
          // Server rejected (e.g. transform produced a no-op it dropped). Treat
          // as acknowledged at whatever revision the server reports, then move on.
          revision = res && res.newRevision ? res.newRevision : revision;
        } else {
          revision = res.newRevision;
        }
        // Promote the buffer to the next inflight op.
        if (buffer !== null) {
          inflight = buffer;
          buffer = null;
          flushInflight();
        } else {
          inflight = null;
        }
      })
      .catch(() => {
        // Socket dropped mid-flight; reconnection triggers a resync (re-join).
      });
  }

  // ── Remote ops → editor ─────────────────────────────────────────────────────
  socket.onReceiveOp = (remoteOp, newRevision) => {
    if (disposed) return;

    // Rebase the remote op past our un-acknowledged local ops, and rebase those
    // past the remote, so everyone converges.
    let toApply = remoteOp;
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

    // Compute every edit's range from the PRE-edit model, then apply in
    // descending start order so earlier edits don't shift later offsets.
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
        // insert does not advance the source offset
      }
    }
    edits.sort((a, b) => b.start - a.start);

    // Preserve the local caret across the remote application.
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

  // ── Presence / remote cursors (optional) ────────────────────────────────────
  const remoteDecorations = new Map(); // peerId -> decoration ids
  const peerColors = new Map();
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
    socket.sendAwareness(documentId, {
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

  function renderRemote(peerId, data) {
    const m = editor.getModel();
    if (!m || !data) return;
    const color = colorFor(peerId, data.color);
    const a = m.getPositionAt(Math.min(data.anchor ?? 0, m.getValueLength?.() ?? Number.MAX_SAFE_INTEGER));
    const h = m.getPositionAt(data.head ?? data.anchor ?? 0);
    ensurePeerStyle(peerId, color, data.name);
    const decos = [{
      range: monaco.Range.fromPositions(a, h),
      options: {
        className: a.equals(h) ? undefined : `collab-remote-selection collab-peer-${cssId(peerId)}`,
        beforeContentClassName: `collab-remote-caret collab-peer-${cssId(peerId)}`,
        stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
      },
    }];
    const prev = remoteDecorations.get(peerId) || [];
    remoteDecorations.set(peerId, editor.deltaDecorations(prev, decos));
  }

  function cssId(peerId) { return peerId.replace(/[^a-zA-Z0-9_-]/g, ""); }

  const injectedStyles = new Set();
  function ensurePeerStyle(peerId, color, name) {
    const id = cssId(peerId);
    if (injectedStyles.has(id)) return;
    injectedStyles.add(id);
    const style = document.createElement("style");
    style.textContent =
      `.collab-peer-${id}.collab-remote-selection{background:${hexToRgba(color, 0.25)};}` +
      `.collab-peer-${id}.collab-remote-caret{border-left:2px solid ${color};margin-left:-1px;}`;
    document.head.appendChild(style);
  }

  function hexToRgba(hex, alpha) {
    const h = hex.replace("#", "");
    const n = parseInt(h.length === 3 ? h.split("").map(c => c + c).join("") : h, 16);
    return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
  }

  socket.onReceiveAwareness = (list) => {
    for (const s of list) {
      try { renderRemote(s.peerId, typeof s.data === "string" ? JSON.parse(s.data) : s.data); }
      catch { /* ignore malformed presence */ }
    }
  };
  socket.onPeerDisconnected = (peerId) => {
    const prev = remoteDecorations.get(peerId);
    if (prev) { editor.deltaDecorations(prev, []); remoteDecorations.delete(peerId); }
  };

  // ── Join / resync ───────────────────────────────────────────────────────────
  async function joinAndSeed() {
    const res = await socket.join(documentId, "text");
    const snapshot = decodeSnapshot(res.snapshot);
    const text = snapshot.content || "";

    applyingRemote = true;
    try {
      const m = editor.getModel();
      m.setEOL(monaco.editor.EndOfLineSequence.LF);
      // Server-wins seed/resync: replace content with the authoritative snapshot.
      if (m.getValue() !== text) m.setValue(text);
    } finally {
      applyingRemote = false;
    }

    revision = res.revision;
    // On resync after a drop, discard un-acked local ops (server-wins).
    inflight = null;
    buffer = null;
  }

  socket.onReconnected = () => { joinAndSeed().catch(() => {}); };

  (async () => {
    try {
      await socket.connect();
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
      changeSub.dispose();
      cursorSub.dispose();
      for (const ids of remoteDecorations.values()) editor.deltaDecorations(ids, []);
      remoteDecorations.clear();
      socket.close();
    },
  };
}
