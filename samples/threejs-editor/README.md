# Collaborative three.js editor (HTML + JS)

Two people editing the **same 3D scene** at once — add a cube in one browser,
it appears in the other; drag it, both see it move. It's the **unmodified
[three.js editor](https://threejs.org/editor/)** made collaborative from the
outside, with zero forks of the editor itself.

This is a strong demonstration of the OpStream thesis: you can make an existing
editor collaborative *without touching it*, as long as it exposes a command /
history hook to tap into.

## How it works

```
index.html
  └─ <iframe src="/three.js-dev/editor/index.html">   ← upstream editor, untouched
                                                         (proxied from threejs.org)
  └─ src/main.js        waits for the iframe's `editor`, then starts a CollabSession
       └─ src/collab-session.js
            • wraps editor.history.execute → captures every local command
            • maps each command to a JSON-CRDT op (objects.<uuid>.position, …)
            • sends/receives ops over SignalR (@microsoft/signalr)
            • applies remote ops back into the three.js scene
                                   │
                                   ▼  /collab  (proxied to the OpStream server)
                          OpStream server — JSON CRDT engine
```

- **Document type:** `json` (the JSON CRDT engine). Each scene object is a
  register at path `objects.<uuid>`, with sub-paths for `position`, `rotation`,
  `scale`, `parent`, `color.*`, etc.
- **Local capture:** `editor.history.execute` is monkey-patched so every command
  is applied locally (as normal) *and* queued for the server.
- **Coalescing:** the outbox is keyed by path, so dragging an object emits one
  op per network round-trip carrying only the latest position — not one per tick.
- **Supported commands:** `Add`, `Remove`, `Move`, `SetPosition`, `SetRotation`,
  `SetScale`, `SetColor`, `SetValue`. Any other command runs locally but is
  intentionally **not** shared (so unsupported edits don't crash a peer).

## Run it

You need an OpStream server with the **SignalR transport** and the **`json`
engine** enabled, reachable at `http://localhost:8080` (its `/collab` hub). The
default Docker image fits:

```bash
docker run -p 8080:8080 opstreamcollab/opstream
```

Then start the sample:

```bash
npm install
npm run dev
```

Vite serves the page on <http://localhost:5173> and proxies:

- `/three.js-dev/*` → `https://threejs.org/*` — so the upstream editor loads
  **same-origin** (no local three.js clone needed; `contentWindow` access works).
- `/collab` → `http://localhost:8080` — the OpStream SignalR hub.

Open <http://localhost:5173> in **two browser tabs** (both join the document
`scene-demo`). Add and move objects in one — watch them appear in the other.

> Pointing at a different server? Change the `/collab` proxy target in
> [`vite.config.js`](vite.config.js), or the `url` in
> [`src/main.js`](src/main.js).

## Notes & limitations

- **Demo-grade.** No auth, fixed `documentId` (`scene-demo`), and only the
  command subset above is synced.
- **JS SignalR client.** This sample talks SignalR directly via the
  `@microsoft/signalr` npm package — handy if you want a SignalR (rather than
  raw WebSocket) JS client to copy from.
- **Op discriminator is `$type`.** The JSON-CRDT op variants use `$type`
  (System.Text.Json's default), e.g. `{ "$type": "set", "path": …, "value": … }`.
  *(The wire-protocol docs currently show a plain `type` field — that's out of
  date; `$type` is what the server binds.)*
