# Collaborative flowchart (GoJS + OpStream)

A polished flowchart/diagram editor where a team builds the same diagram live:
add nodes, drag them, wire them together, edit labels — everyone sees it instantly.
[GoJS](https://gojs.net) is the most capable diagramming library here; this is
the same "collaborativize from the outside" pattern as the
[three.js](../threejs-editor) and [Fabric.js](../fabric-collab) samples.

GoJS is an unusually clean fit: its model emits a `ModelChanged` event per
transaction and exposes first-class mutation methods, so capture and apply are
both straightforward.

> ⚠️ **License.** GoJS is **commercial** (Northwoods Software). The free
> **evaluation** build used here works identically but **watermarks** the canvas
> with "GoJS Evaluation". That's fine for building, testing, and demoing while
> you evaluate. A production deployment needs a GoJS license — unlike the other
> samples here, which use MIT/permissive libraries.

Beyond live sync it also shows **presence** (who's here + colored edit feedback
on the touched node) and **anchored comments** (a 💬 pin per commented node + a
side panel).

## Node types

Four templates, selected by the node data's `category` (so the shape syncs
automatically through the JSON register): **Process** (rounded box), **Decision**
(diamond), **Start/End** (capsule/terminator) and **Data** (parallelogram).
Add them from the toolbar.

## How it works

- **Document type `json`** (JSON CRDT engine). Each node is a register at
  `nodes.<key>`, each link at `links.<key>`.
- **Stable identity:** string **uuid keys** via `makeUniqueKeyFunction` /
  `makeUniqueLinkKeyFunction` — GoJS' default integer keys are per-model and
  would collide across peers.
- **Capture:** on every finished transaction (`addModelChangedListener` +
  `e.isTransactionFinished`) we diff the model's `nodeDataArray`/`linkDataArray`
  against our last snapshot and emit `set`/`del` per changed key — robust, no
  reliance on ChangedEvent internals.
- **Apply:** remote ops run inside a guarded `diagram.commit` using
  `addNodeData` / `setDataProperty` / `removeNodeData` (+ link equivalents);
  nodes are applied before links.
- **Transport:** SignalR via `@microsoft/signalr` to `/collab`.

> Op discriminator is **`$type`** (`{ "$type": "set", "path": "nodes.k-ab12", … }`).

### Presence & comments (verified server wire contract)

- **Awareness** is pushed as the **`ReceiveAwarenessUpdate`** event (NOT
  `ReceiveAwareness`), one `AwarenessState` `{ peerId: <connId>, data, lastUpdated }`
  where `data` is the `{ peerId, name, color }` we sent via `UpdateAwareness`. The
  server **excludes the sender** and only emits on change, so each peer
  **re-broadcasts** when it first sees a newcomer (plus an 8 s heartbeat) — that's
  how a late joiner appears in everyone's "Who's here".
- **Edit feedback:** every op carries the sender's `peerId`; on a remote node op
  we flash that peer's name + color label and briefly glow the node.
- **Comments** use the real DTO: `anchor: { kind, data }` (we anchor with
  `{ kind: 'gojs-node', data: { key } }` — a custom kind, no rebasing needed since
  node keys are stable), `authorPeerId` (the server ConnectionId, mapped to the
  presence name), and **resolved = `resolvedAt != null`**. There is **no
  `anchorJson` and no `isResolved`** field.

## Run it

```bash
docker run -p 8080:8080 opstreamcollab/opstream   # server: SignalR + json engine
```
```bash
cd samples/gojs-collab
npm install && npm run dev
```

Open <http://localhost:5173> in two tabs (both join `flow-demo`). Add nodes, drag
them, link them (drag from a node's edge to another), double-click to edit text.

> The dev proxy points `/collab` at `http://localhost:50109` — change it in
> [`vite.config.js`](vite.config.js) (the Docker image listens on `:8080`).

## Verify in a real test

- `makeUniqueKeyFunction` / `makeUniqueLinkKeyFunction` and
  `model.findLinkDataForKey` — confirm names against your GoJS version.
- Per-key last-writer-wins: two people transforming the *same* node/link at once,
  one wins (independent elements never conflict).
- No auth; fixed `documentId` (`flow-demo`).
