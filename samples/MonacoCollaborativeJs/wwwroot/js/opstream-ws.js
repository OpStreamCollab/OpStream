// opstream-ws.js
// Thin WebSocket envelope over OpStream's raw WebSocket transport
// (src/OpStream.Server.Transports.WebSockets/WebSocketTransport.cs).
//
// Wire facts (verified against the server):
//  - Frames are UTF-8 JSON of WebSocketMessage, camelCase property names.
//  - `messageType` is a C# enum serialized as an INTEGER (see MsgType below).
//  - byte[] fields (`snapshot`, `payload`) are base64 strings; once decoded they
//    are UTF-8 JSON of the engine state / op.
//  - Server echoes `correlationId` on JoinResponse / OpResponse / ErrorResponse.
//  - The server does NOT echo a peer's own op back to it (sender is excluded),
//    so no self-echo suppression is required on the client.

export const MsgType = {
  JoinRequest: 0,
  JoinResponse: 1,
  OpRequest: 2,
  OpResponse: 3,
  AwarenessRequest: 4,
  ReceiveOpEvent: 5,
  ReceiveAwarenessEvent: 6,
  PeerDisconnectedEvent: 7,
  ErrorResponse: 8,
};

// ── base64 <-> bytes <-> JSON helpers ────────────────────────────────────────
function bytesToBase64(bytes) {
  let bin = "";
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    bin += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
  }
  return btoa(bin);
}
function base64ToBytes(b64) {
  const bin = atob(b64);
  const arr = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
  return arr;
}
export function encodeOp(op) {
  return bytesToBase64(new TextEncoder().encode(JSON.stringify(op)));
}
export function decodeOp(b64) {
  return JSON.parse(new TextDecoder().decode(base64ToBytes(b64)));
}
export function decodeSnapshot(b64) {
  if (!b64) return { content: "" };
  const text = new TextDecoder().decode(base64ToBytes(b64));
  if (!text) return { content: "" };
  try { return JSON.parse(text); } catch { return { content: text }; }
}

function defaultUrl(path) {
  const proto = location.protocol === "https:" ? "wss" : "ws";
  return `${proto}://${location.host}${path}`;
}

/**
 * Manages one WebSocket connection to the OpStream collaboration endpoint.
 * Auto-reconnects with backoff and surfaces server-push events via callbacks.
 */
export class OpStreamSocket {
  /**
   * @param {object} opts
   * @param {string} [opts.url]       Full ws(s):// URL. Defaults to same-origin /collab-ws.
   * @param {string} [opts.path]      Path used when url is omitted. Default "/collab-ws".
   * @param {() => (string|null|undefined)} [opts.getAuthToken] Optional token appended as ?access_token=.
   */
  constructor(opts = {}) {
    const base = opts.url || defaultUrl(opts.path || "/collab-ws");
    this._baseUrl = base;
    this._getAuthToken = opts.getAuthToken || (() => null);
    this._ws = null;
    this._pending = new Map();        // correlationId -> { resolve, reject }
    this._reconnectDelay = 500;
    this._closedByUser = false;

    // Event handlers (assignable by the caller).
    this.onReceiveOp = null;          // (op, newRevision) => void
    this.onReceiveAwareness = null;   // (awarenessArray) => void
    this.onPeerDisconnected = null;   // (peerId) => void
    this.onStatus = null;             // ("connecting"|"open"|"closed") => void
    this.onReconnected = null;        // () => void  (fired after a non-initial reconnect)
    this._everConnected = false;
  }

  connect() {
    return new Promise((resolve, reject) => {
      this._closedByUser = false;
      this._emitStatus("connecting");

      let url = this._baseUrl;
      const token = this._getAuthToken();
      if (token) url += (url.includes("?") ? "&" : "?") + "access_token=" + encodeURIComponent(token);

      const ws = new WebSocket(url);
      this._ws = ws;

      ws.onopen = () => {
        this._reconnectDelay = 500;
        this._emitStatus("open");
        if (this._everConnected && this.onReconnected) this.onReconnected();
        this._everConnected = true;
        resolve();
      };

      ws.onmessage = (ev) => this._handleMessage(ev.data);

      ws.onerror = () => { /* close handler drives reconnect */ };

      ws.onclose = () => {
        this._emitStatus("closed");
        // Fail any in-flight requests.
        for (const { reject: rj } of this._pending.values()) rj(new Error("socket closed"));
        this._pending.clear();
        if (this._closedByUser) return;
        // Backoff reconnect.
        const delay = this._reconnectDelay;
        this._reconnectDelay = Math.min(this._reconnectDelay * 2, 10000);
        setTimeout(() => { if (!this._closedByUser) this.connect().catch(() => {}); }, delay);
      };

      // If the very first connect fails, reject the promise too.
      ws.addEventListener("close", () => { if (!this._everConnected) reject(new Error("connect failed")); }, { once: true });
    });
  }

  _emitStatus(s) { if (this.onStatus) this.onStatus(s); }

  _send(msg) {
    if (!this._ws || this._ws.readyState !== WebSocket.OPEN) throw new Error("socket not open");
    this._ws.send(JSON.stringify(msg));
  }

  _request(msg) {
    const correlationId = (crypto.randomUUID && crypto.randomUUID()) ||
      (Date.now() + "-" + Math.random().toString(16).slice(2));
    msg.correlationId = correlationId;
    return new Promise((resolve, reject) => {
      this._pending.set(correlationId, { resolve, reject });
      try { this._send(msg); }
      catch (e) { this._pending.delete(correlationId); reject(e); }
    });
  }

  _handleMessage(data) {
    let msg;
    try { msg = JSON.parse(data); } catch { return; }

    // Correlated responses first.
    if (msg.correlationId && this._pending.has(msg.correlationId)) {
      const { resolve, reject } = this._pending.get(msg.correlationId);
      this._pending.delete(msg.correlationId);
      if (msg.messageType === MsgType.ErrorResponse) reject(new Error(msg.errorMessage || "server error"));
      else if (msg.messageType === MsgType.JoinResponse) resolve(msg.joinResponse);
      else if (msg.messageType === MsgType.OpResponse) resolve(msg.opResponse);
      else resolve(msg);
      return;
    }

    // Server-push events.
    switch (msg.messageType) {
      case MsgType.ReceiveOpEvent:
        if (this.onReceiveOp && msg.receiveOpEvent) {
          this.onReceiveOp(decodeOp(msg.receiveOpEvent.payload), msg.receiveOpEvent.newRevision);
        }
        break;
      case MsgType.ReceiveAwarenessEvent:
        if (this.onReceiveAwareness && msg.receiveAwarenessEvent) {
          this.onReceiveAwareness(msg.receiveAwarenessEvent.awareness || []);
        }
        break;
      case MsgType.PeerDisconnectedEvent:
        if (this.onPeerDisconnected && msg.peerDisconnectedEvent) {
          this.onPeerDisconnected(msg.peerDisconnectedEvent.peerId);
        }
        break;
    }
  }

  // ── Public API ─────────────────────────────────────────────────────────────

  /** Join a document. Resolves with { revision, snapshot(base64), awareness }. */
  join(documentId, documentType = "text") {
    return this._request({
      messageType: MsgType.JoinRequest,
      joinRequest: { documentId, documentType, clientProtoVersion: 1 },
    });
  }

  /** Send an op. `payloadB64` from encodeOp(op). Resolves with { success, newRevision, errorMessage }. */
  sendOp(documentId, payloadB64, baseRevision) {
    return this._request({
      messageType: MsgType.OpRequest,
      opRequest: { documentId, payload: payloadB64, baseRevision },
    });
  }

  /** Fire-and-forget presence update. `data` is an arbitrary JSON-serialisable object. */
  sendAwareness(documentId, data) {
    if (!this._ws || this._ws.readyState !== WebSocket.OPEN) return;
    this._send({
      messageType: MsgType.AwarenessRequest,
      awarenessRequest: { documentId, dataJson: JSON.stringify(data) },
    });
  }

  close() {
    this._closedByUser = true;
    try { this._ws && this._ws.close(1000, "client closing"); } catch { /* ignore */ }
  }
}
