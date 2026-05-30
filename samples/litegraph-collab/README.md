# Collaborative node editor (LiteGraph + OpStream)

Two people building the **same node graph** at once: add a node in one browser,
it appears in the other; drag it, they all see it move; wire nodes together and
the connection syncs. [LiteGraph.js](https://github.com/jagenjo/litegraph.js) is
used **unmodified** (from its CDN) — collaboration is bolted on from the outside,
same pattern as the [three.js](../threejs-editor) and [Luckysheet](../luckysheet-collab)
samples.

LiteGraph is a particularly clean fit: `graph.add(node)` **preserves a
preassigned `node.id`**, so node identity is stable across peers (no id-mapping
glue needed).

## How it works

- **Document type `json`** (JSON CRDT engine).
- **Structure** (add / remove / connect) → one register `graph` holding
  `graph.serialize()`; applied remotely with `graph.configure()`, which rebuilds
  nodes **and links** with their original ids.
- **Dragging** → granular `nodes.<id>.pos` ops, so a drag doesn't reserialize
  the whole graph every frame.
- **Capture:** `graph.onNodeAdded` / `onNodeRemoved` / `onConnectionChange` and
  `LGraphCanvas.onNodeMoved`. **Apply:** guarded by a depth counter so
  `configure()`'s own `onNodeAdded` callbacks aren't echoed back out.
- **Transport:** SignalR via `@microsoft/signalr` to `/collab`.

> Op discriminator is **`$type`** (`{ "$type": "set", "path": "graph", … }`).

## Run it

```bash
docker run -p 8080:8080 opstreamcollab/opstream   # server: SignalR + json engine
```
```bash
cd samples/litegraph-collab
npm install && npm run dev
```

Open <http://localhost:5173> in two tabs (both join `graph-demo`). Use **+ Const**
/ **+ Watch** (or right-click the canvas for more node types), drag nodes, and
wire outputs to inputs.

> The dev proxy points `/collab` at `http://localhost:50109` — change it in
> [`vite.config.js`](vite.config.js) to match your server (the Docker image
> listens on `:8080`).

## Limitations (demo)

- Structural changes use **whole-graph last-writer-wins**: two people editing the
  graph *structure* at the exact same moment can clobber each other (positions are
  granular and don't). Fine for a demo; finer-grained per-node/per-link ops would
  remove it.
- No auth; fixed `documentId` (`graph-demo`).
- Live edits to node *property values* aren't separately synced — they ride along
  on the next structural change.
