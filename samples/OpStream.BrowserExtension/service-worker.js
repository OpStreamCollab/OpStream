// service-worker.js  (MV3 background, ES module)
//
// Owns the actual WebSocket to the OpStream server. Running the socket here —
// in the EXTENSION's context — means it is governed by the extension's CSP, not
// the host page's. So even pages whose `connect-src` would block a direct
// content-script socket can still collaborate: the content script relays through
// a chrome.runtime Port, and this worker proxies to the server.
//
//   content script  ──Port "opstream"──▶  service worker  ──WebSocket──▶  server
//
// One Port == one attachment == one WebSocketOpStreamClient. The open port keeps
// the worker alive while a field is live; the content side also pings periodically.

import { WebSocketOpStreamClient } from "./collab/WebSocketOpStreamClient.js";

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== "opstream") return;

  let client = null;

  port.onMessage.addListener(async (msg) => {
    try {
      switch (msg.kind) {
        case "ping":
          // No-op: just resetting the worker's idle timer.
          break;

        case "join": {
          client = new WebSocketOpStreamClient(msg.url);
          client.onReceiveOp = (payload, newRevision) =>
            safePost(port, { kind: "receiveOp", payload, newRevision });
          client.onReceiveAwareness = (awareness) =>
            safePost(port, { kind: "receiveAwareness", awareness });
          client.onPeerDisconnected = (peerId) =>
            safePost(port, { kind: "peerDisconnected", peerId });
          client.onDisconnected = (err) =>
            safePost(port, { kind: "status", status: "closed", error: err && err.message });

          const result = await client.connectAndJoinAsync(msg.documentId, msg.documentType);
          safePost(port, { kind: "reply", reqId: msg.reqId, ok: true, result });
          break;
        }

        case "op": {
          if (!client) throw new Error("Not joined");
          const result = await client.sendOpAsync(msg.documentId, msg.payload, msg.baseRevision);
          safePost(port, { kind: "reply", reqId: msg.reqId, ok: true, result });
          break;
        }

        case "awareness":
          if (client) client.sendAwarenessAsync(msg.documentId, msg.data);
          break;

        case "dispose":
          if (client) { client.disposeAsync(); client = null; }
          break;
      }
    } catch (err) {
      safePost(port, { kind: "reply", reqId: msg.reqId, ok: false, error: (err && err.message) || String(err) });
    }
  });

  port.onDisconnect.addListener(() => {
    if (client) { client.disposeAsync(); client = null; }
  });
});

function safePost(port, message) {
  try { port.postMessage(message); } catch { /* port already closed */ }
}
