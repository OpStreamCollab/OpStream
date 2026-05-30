// sw-port-client.js
// A drop-in replacement for WebSocketOpStreamClient that owns NO socket itself.
// Instead it relays every call to the background service worker over a long-lived
// chrome.runtime Port, so the actual WebSocket lives in the extension's context
// and escapes the host page's CSP. Exposes the exact same surface that
// textarea-collab.js consumes, so the OT state machine is unchanged.

const KEEPALIVE_MS = 20000; // ping so the (event-based) service worker stays alive

export class SwPortOpStreamClient {
  constructor(url) {
    this.url = url;

    // Same event hooks as WebSocketOpStreamClient.
    this.onReceiveOp = null;
    this.onReceiveAwareness = null;
    this.onPeerDisconnected = null;
    this.onDisconnected = null;

    this._pending = new Map();
    this._reqSeq = 0;

    this._port = chrome.runtime.connect({ name: "opstream" });
    this._port.onMessage.addListener((msg) => this._onMessage(msg));
    this._port.onDisconnect.addListener(() => {
      if (this.onDisconnected) this.onDisconnected(new Error("Service worker port disconnected"));
    });

    this._keepalive = setInterval(() => {
      try { this._port.postMessage({ kind: "ping" }); } catch { /* closed */ }
    }, KEEPALIVE_MS);
  }

  _onMessage(msg) {
    switch (msg.kind) {
      case "reply": {
        const p = this._pending.get(msg.reqId);
        if (p) {
          this._pending.delete(msg.reqId);
          if (msg.ok) p.resolve(msg.result);
          else p.reject(new Error(msg.error || "Service worker error"));
        }
        break;
      }
      case "receiveOp":
        if (this.onReceiveOp) this.onReceiveOp(msg.payload, msg.newRevision);
        break;
      case "receiveAwareness":
        if (this.onReceiveAwareness) this.onReceiveAwareness(msg.awareness);
        break;
      case "peerDisconnected":
        if (this.onPeerDisconnected) this.onPeerDisconnected(msg.peerId);
        break;
      case "status":
        if (msg.status === "closed" && this.onDisconnected) {
          this.onDisconnected(new Error(msg.error || "closed"));
        }
        break;
    }
  }

  _request(payload) {
    const reqId = ++this._reqSeq;
    return new Promise((resolve, reject) => {
      this._pending.set(reqId, { resolve, reject });
      try {
        this._port.postMessage({ ...payload, reqId });
      } catch (err) {
        this._pending.delete(reqId);
        reject(err);
      }
    });
  }

  async connectAndJoinAsync(documentId, documentType) {
    return this._request({ kind: "join", url: this.url, documentId, documentType });
  }

  async sendOpAsync(documentId, payload, baseRevision) {
    return this._request({ kind: "op", documentId, payload, baseRevision });
  }

  async sendAwarenessAsync(documentId, data) {
    try { this._port.postMessage({ kind: "awareness", documentId, data }); } catch { /* closed */ }
  }

  async disposeAsync() {
    clearInterval(this._keepalive);
    try { this._port.postMessage({ kind: "dispose" }); } catch { /* closed */ }
    try { this._port.disconnect(); } catch { /* closed */ }
    for (const p of this._pending.values()) p.reject(new Error("Client disposed"));
    this._pending.clear();
  }
}
