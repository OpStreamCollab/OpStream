# Collaborative Kanban board (SortableJS + OpStream)

A Trello-style board where a whole team moves cards at once: add a card in one
browser, drag it between columns, edit its text — everyone sees it live. Built
with plain DOM + [SortableJS](https://github.com/SortableHQ/Sortable) (MIT) for
drag-and-drop; collaboration is added over OpStream — same pattern as the
[three.js](../threejs-editor) and [Luckysheet](../luckysheet-collab) samples,
but a **business app** rather than an editor.

## How it works

- **Document type `json`** (JSON CRDT engine). Each card is a register at
  `cards.<id>` = `{ text, column, order }`.
- **Capture:** adding, editing (contenteditable blur), dragging (SortableJS
  `onEnd`) or deleting a card → a JSON `set`/`del` op, coalesced per card.
- **Apply:** remote ops update the local card map and re-render the board.
- **Ordering:** a dragged card's `order` becomes the midpoint between its new
  neighbours, so reordering one card doesn't renumber the rest.
- **Transport:** SignalR via `@microsoft/signalr` to `/collab`.

> Op discriminator is **`$type`** (`{ "$type": "set", "path": "cards.c-ab12", … }`).

## Run it

```bash
docker run -p 8080:8080 opstreamcollab/opstream   # server: SignalR + json engine
```
```bash
cd samples/kanban-collab
npm install && npm run dev
```

Open <http://localhost:5173> in two tabs (both join `kanban-demo`). Add, drag,
edit, and delete cards.

> The dev proxy points `/collab` at `http://localhost:50109` — change it in
> [`vite.config.js`](vite.config.js) (the Docker image listens on `:8080`).

## Limitations (demo)

- Whole-board re-render on each remote op; if you're mid-edit on a card when a
  remote op lands, your caret may jump (fine for a demo).
- No auth; fixed `documentId` (`kanban-demo`); columns are hardcoded.
