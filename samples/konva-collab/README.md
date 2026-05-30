# OpStream + Konva — Collaborative Canvas Editor

Real-time multi-user canvas editor powered by **[Konva.js](https://konvajs.org)** (MIT)
and **[OpStream](https://opstream.stream)** JSON CRDT sync.

## What it does

- Add rectangles, circles, and text to a shared canvas
- Drag shapes to reposition — changes sync instantly to all peers
- Resize and rotate any shape with the built-in Transformer
- Double-click a Text shape to edit its content inline
- Change fill color, stroke color, and stroke width per shape
- Delete shapes (button or `Delete`/`Backspace` key)
- See who else is online with colored presence indicators
- Remote edits flash a colored border + name label on the modified shape
- Leave anchored comments on any shape; resolve them when done

## How it works

**Engine:** OpStream **JSON CRDT**, one register per shape:

```
shapes.<uuid>  →  { className: "Rect" | "Circle" | "Text", attrs: { … } }
```

Each shape stores its full Konva `getAttrs()` state: position, size, scale,
rotation, fill, stroke, text content, etc.

**Capture events (local → server):**
- `dragend` on the stage — position changed
- `transformend` on the Transformer — scale/rotation changed
- Direct `session.emitShape()` calls after fill/stroke/text edits
- `session.emitDelete()` after destroying a shape

**Apply events (server → local):**
- `set` op on existing shape → `node.setAttrs(attrs)` + `layer.batchDraw()`
- `set` op on new shape → `new Konva[className](attrs)` + `layer.add()`
- `del` op → `node.destroy()` + clear selection if it was selected

**Event delegation:** `stage.on('click tap')`, `stage.on('dragend')`, and
`stage.on('dblclick')` capture interactions on ALL shapes — including ones
created by remote peers — without needing per-node listener wiring.

## How to run

```bash
# 1. Start the OpStream server (Docker, simplest)
docker run -p 5555:8080 opstreamcollab/opstream

# 2. Install & start the dev server
npm install
npm run dev
```

Open two browser tabs at `http://localhost:5173` — edits in one appear
instantly in the other. Each tab gets a random username and color.

## Limitations / unverified points

- `getClientRect({ relativeTo: layer })` is used for overlay pin positioning.
  If Konva's coordinate semantics differ from expectations, pins may be slightly
  offset (adjust the `shapeBBox()` function in `main.js`).
- Text `width()` after creation may be `0` until the node is rendered. The text
  editing textarea may appear narrow initially; this doesn't affect sync.
- Images are not included to keep the sample self-contained (async `Konva.Image.fromURL`
  and URL sharing across peers adds complexity).
- Konva is MIT-licensed; this sample uses the public CDN build.
