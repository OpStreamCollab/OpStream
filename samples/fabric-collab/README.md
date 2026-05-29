# Collaborative canvas (Fabric.js + OpStream)

A shared 2D design canvas: drop rectangles, circles and text, then move, resize,
rotate and edit them with several people at once — everyone sees every change
live. [Fabric.js](https://github.com/fabricjs/fabric.js) (MIT) is used unmodified
from its CDN; collaboration is added over OpStream — the 2D sibling of the
[three.js sample](../threejs-editor).

## How it works

- **Document type `json`** (JSON CRDT engine). Each canvas object is a register
  at `objects.<id>` holding `obj.toObject(['id'])`; we assign a stable `id` on
  creation.
- **Capture:** Fabric's `object:added` / `object:modified` / `object:removed`
  events → JSON `set`/`del` ops, coalesced per object. `object:modified` fires on
  release, so a drag sends one op, not one per frame.
- **Apply:** remote ops re-enliven the object onto the canvas
  (`fabric.util.enlivenObjects`), guarded by a depth counter so re-adding doesn't
  echo back out.
- **Transport:** SignalR via `@microsoft/signalr` to `/collab`.

> Op discriminator is **`$type`** (`{ "$type": "set", "path": "objects.o-ab12", … }`).

## Run it

```bash
docker run -p 8080:8080 opstreamcollab/opstream   # server: SignalR + json engine
```
```bash
cd samples/fabric-collab
npm install && npm run dev
```

Open <http://localhost:5173> in two tabs (both join `canvas-demo`). Add shapes,
drag/resize/rotate them, edit text.

> The dev proxy points `/collab` at `http://localhost:50109` — change it in
> [`vite.config.js`](vite.config.js) (the Docker image listens on `:8080`).

## Notes & limitations

- **Targets Fabric v5** (callback-style `enlivenObjects`). The CDN is pinned to
  `fabric@5.3.0`. Fabric **v6** returns a Promise from `enlivenObjects` — switch
  to `const [o] = await fabric.util.enlivenObjects([value])` if you upgrade.
- Per-object last-writer-wins: two people transforming the *same* object at once,
  one wins (different objects never conflict).
- No auth; fixed `documentId` (`canvas-demo`); live text-typing syncs on edit
  commit (`object:modified`), not per keystroke.
