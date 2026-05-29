# Collaborative spreadsheet (Luckysheet + OpStream)

Two people editing the **same spreadsheet** live: type in a cell in one browser,
it appears in the other. The [Luckysheet](https://github.com/dream-num/Luckysheet)
spreadsheet is used **unmodified** (loaded from its CDN); collaboration is added
from the outside — same pattern as the [three.js editor sample](../threejs-editor).

## How it works

- **Document type `json`** (the JSON CRDT engine). Each cell is a register at
  `cells.<row>_<col>`.
- **Capture:** Luckysheet's `cellUpdated(r, c, old, new)` hook → a JSON `set`
  (or `del` when cleared) op, coalesced per cell.
- **Apply:** remote ops call `luckysheet.setCellValue(r, c, value)`, with
  value-based echo suppression so re-applying doesn't loop.
- **Transport:** SignalR via `@microsoft/signalr` to the `/collab` hub.

> Op discriminator is **`$type`** (`{ "$type": "set", "path": "cells.0_0", … }`).

## Run it

```bash
docker run -p 8080:8080 opstreamcollab/opstream   # server: SignalR + json engine
```
```bash
cd samples/luckysheet-collab
npm install && npm run dev
```

Open <http://localhost:5173> in two tabs (both join document `sheet-demo`) and
edit cells. Vite proxies `/collab` to the server (see [`vite.config.js`](vite.config.js)).

## Limitations (demo)

- **Only cell values** sync — not formatting, formulas, merges, or row/column
  inserts. Those are a larger surface to map; the value path shows the pattern.
- No auth; fixed `documentId` (`sheet-demo`).
- `setCellValue` triggers `cellUpdated`; echoes are dropped by value match. If a
  genuine same-value edit races, it's harmlessly skipped.
