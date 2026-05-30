# OpStream Collab — Browser Extension prototype

Proof-of-concept Chrome/Edge **Manifest V3** extension that turns a plain
`<textarea>` or text `<input>` on **any web page** into a live collaborative
field backed by an OpStream server — without that page knowing anything about it.

This validates the "extension as a universal injection vehicle" idea: the hard
part is never the extension, it's the **document ↔ CRDT/OT adapter**. This
prototype ships the *simple-case* adapter (flat text fields). Rich editors
(`contenteditable`, Google Docs, canvas) each need their own adapter — see
[`radzen-collab-adapter.js`](../OpStream.CollabHtmlEditor/wwwroot/js/radzen-collab-adapter.js)
for the contenteditable pattern and [`monaco-collab.js`](../MonacoCollaborativeJs/js/monaco-collab.js)
for Monaco.

## How it works

```
content-loader.js  (classic content script, isolated world)
   │  injects a floating panel + lets you click-to-pick a text field
   │  dynamically imports the ES-module adapter:
   ▼
collab/textarea-collab.js   ── attachTextareaCollab(field, opts)
   ├─ ot-text.js                  OT engine (verbatim port of the server engine)
   └─ transport (one of):
        • WebSocketOpStreamClient.js   direct WS from the page context (default)
        • sw-port-client.js            relays to the service worker (CSP bypass)
                                       │
                                       ▼
                          OpStream server  (ws://localhost:50109/collab-ws)
```

### Bypassing the page's CSP — "Route via service worker"

A WebSocket opened from the content script runs in the page's renderer and is
subject to the **host page's** `connect-src` CSP. Sites with a strict CSP can
therefore block a direct connection to your server.

Tick **"Route via service worker"** in the panel and the socket moves into the
background service worker instead:

```
textarea ←→ content script ──chrome.runtime Port──▶ service worker ──WebSocket──▶ OpStream
  (page context, CSP applies)                       (extension context, page CSP does NOT apply)
```

`sw-port-client.js` is a drop-in replacement for `WebSocketOpStreamClient` — same
method surface — so the OT state machine in `textarea-collab.js` is untouched; it
just talks to a proxy that forwards over the Port. The open Port (plus a 20s
keepalive ping) keeps the event-based worker alive while a field is live.

> **Note on the two failure modes they don't both fix:**
> - *Page CSP blocks your origin* → the service-worker route fixes this.
> - *A proxy/firewall kills the WS `Upgrade` itself* → that needs a transport
>   with HTTP fallbacks (SignalR: WS → SSE → long-polling). Not implemented here;
>   the cleanest design is to run a SignalR client **inside** the service worker,
>   getting both the CSP bypass and the transport fallback.

- Each `input` event is turned into a single contiguous `TextOp` via a
  prefix/suffix diff (`diffToOp` in `ot-text.js`).
- The standard **inflight + buffer** OT state machine (identical to the Monaco
  adapter) keeps local typing and remote ops convergent against the server.
- Remote ops are applied to `field.value`; the caret is transformed through the
  op (`transformOffset`) so it survives concurrent edits.
- The document ID defaults to `ext:<origin><path>#fieldN` so the *same field on
  the same URL* in another browser joins the same document. Override it in the
  panel to share across different pages.

The OT engine and WS client are **copied verbatim** from the Monaco sample so
the extension is self-contained and packageable.

## Run it

1. **Start an OpStream server** that exposes the WebSocket transport at
   `/collab-ws`. The Monaco sample already does this on port `50109` — reuse it,
   or run `src/OpStream.Host` and point the panel's "Server" field at its
   `WebSockets:Path` (`/collab-ws`).

2. **Load the extension** (Chrome/Edge):
   - Go to `chrome://extensions`, enable **Developer mode**.
   - **Load unpacked** → select this folder (`samples/OpStream.BrowserExtension`).

3. **Try it:** open any page with a `<textarea>` (e.g. a blog comment box, a
   GitHub issue draft, `https://example.com` + devtools, or a local test page).
   - Click **1 · Pick a text field**, then click the field.
   - Click **2 · Make collaborative**.
   - Open the *same URL* in a second browser/profile, attach the same field —
     typing in one shows up in the other.

## Limitations (by design, for the prototype)

- **Flat text only.** `contenteditable`, Monaco, CodeMirror, Google Docs, canvas
  editors are *not* supported here — they need an editor-specific adapter.
- **No remote-cursor overlay** for textareas yet (presence peers are counted but
  not drawn as carets — that needs a mirror-div to map char offset → x/y).
- **Mixed line endings:** the field value is sent as-is; the server normalizes
  to LF for `text` documents, same as the other samples.
- **CSP / network:** a direct content-script socket is subject to the page CSP.
  Tick **"Route via service worker"** to proxy through the background worker and
  bypass it (see above). A WS-`Upgrade`-killing proxy still needs a fallback
  transport (SignalR) — not implemented yet.

## Files

| File | Role |
|------|------|
| `manifest.json` | MV3 manifest — injects on `*://*/*`, registers the SW, exposes `collab/*` as web-accessible modules |
| `service-worker.js` | Background worker that owns the WebSocket (CSP-bypass route) |
| `content-loader.js` | Floating panel + field picker (classic content script) |
| `content.css` | Panel styling + field highlight outlines |
| `collab/textarea-collab.js` | The textarea ↔ OpStream OT adapter (transport-agnostic) |
| `collab/sw-port-client.js` | Transport proxy: relays to the service worker over a Port |
| `collab/ot-text.js` | OT engine (verbatim from Monaco sample, + `diffToOp`) |
| `collab/WebSocketOpStreamClient.js` | Direct WS transport (verbatim from Monaco sample) |
