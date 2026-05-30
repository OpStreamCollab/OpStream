# Recipe: Collaborative 3D editor

!!! example "Try it live"
    **▶ [Live demo](https://hostdemo.opstream.stream/samples/threejs/)** ·
    **[&lt;/&gt; Source](https://github.com/OpStreamCollab/OpStream/tree/main/samples/threejs-editor)** ·
    [all samples](../samples.md)

Take the **[three.js editor](https://threejs.org/editor/)** — a full 3D scene
editor you didn't write — and make it multiplayer. Add a cube in one browser, it
appears in everyone's; drag it, they all see it move. **Without forking the
editor.**

This is the most general pattern OpStream enables: *collaborativize an editor you
don't own.* The full sample is
[`samples/threejs-editor`](https://github.com/OpStreamCollab/OpStream/tree/main/samples/threejs-editor).

## What you're building

- The upstream three.js editor, loaded **unmodified** in an `<iframe>`.
- The **JSON CRDT** engine — each scene object is a register at
  `objects.<uuid>`, with sub-paths for `position`, `rotation`, `color`, …
- A JavaScript **SignalR** client (`@microsoft/signalr`).

## The trick: wrap, don't fork

Any editor can be made collaborative from the outside if it gives you **two
hooks**:

1. **Observe local edits** — a change/command/history event you can listen to.
2. **Apply remote edits** — a programmatic API to mutate its state.

The three.js editor has both: a `history.execute(command)` pipeline, and a scene
API (`addObject`, `objectByUuid`, …). We tap the first to *send* and use the
second to *receive*.

## Capture local edits → ops

Monkey-patch the editor's history so every local command is applied as normal
**and** mapped to a JSON-CRDT op:

```javascript
const original = history.execute.bind(history);
history.execute = (cmd, name) => {
    original(cmd, name);                 // editor behaves exactly as before
    if (remoteApplyDepth > 0) return;    // don't re-send ops we just applied
    const ops = commandToOps(cmd);       // e.g. SetPositionCommand → set objects.<uuid>.position
    if (ops?.length) enqueue(ops);
};

function commandToOps(cmd) {
    const uuid = cmd.object?.uuid;
    const base = `objects.${uuid}`;
    switch (cmd.type) {
        case 'AddObjectCommand':   return [set(base, cmd.object.toJSON())];
        case 'SetPositionCommand': return [set(`${base}.position`, cmd.newPosition.toArray())];
        // …SetRotation, SetScale, SetColor, Remove, Move
    }
}
```

> The op's polymorphic discriminator is **`$type`** (`{ "$type": "set", … }`),
> not `type` — that's what the JSON engine binds.

Outgoing ops are coalesced per path, so dragging an object sends one op per
network round-trip carrying only the latest position — not one per frame.

## Apply remote edits → scene

Incoming ops are routed back into the scene by path, guarded so they don't echo
back out as local edits:

```javascript
connection.on('ReceiveOp', (payload, revision) => {
    remoteApplyDepth++;
    try {
        for (const op of decode(payload).operations) applyPath(op.path, op.value);
    } finally { remoteApplyDepth--; }
});

// applyPath('objects.<uuid>.position', [x,y,z]) →
//   obj.position.fromArray(value); editor.signals.objectChanged.dispatch(obj);
```

## Run it

```bash
docker run -p 8080:8080 opstreamcollab/opstream   # server: SignalR + json engine
```
```bash
cd samples/threejs-editor
npm install && npm run dev
```

Open <http://localhost:5173> in two tabs. Vite proxies the three.js editor from
the official CDN (same-origin) and `/collab` to the server — see the
[sample README](https://github.com/OpStreamCollab/OpStream/tree/main/samples/threejs-editor).

## Why this generalizes

Swap three.js for **any** editor that exposes the same two hooks and you get
collaboration for free — a 2D canvas (Fabric.js, Konva), a diagram editor, a
node graph, a spreadsheet. Only the *command ↔ op* mapping changes; the OpStream
plumbing stays identical.
