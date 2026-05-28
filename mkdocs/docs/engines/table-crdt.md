# Table CRDT Engine

CRDT for **tabular data** — spreadsheets, Airtable-style bases, grid
views. Per-row + per-column + per-cell LWW with sticky tombstones.

## When to use

- Real-time spreadsheets / grids.
- Database-like tables with collaborative row / column editing.
- Any UI where users add / remove / reorder rows and columns while
  others edit cells.

## Types

`TDoc` = `TableDocument`:

| Field | Description |
|---|---|
| `Rows` | `Dictionary<rowId, RowMeta>` — alive or tombstoned. |
| `Columns` | `Dictionary<columnId, ColumnMeta>` — same. |
| `Cells` | `Dictionary<CellAddress, CellRegister>` — `CellAddress = (rowId, columnId)`. |

`RowMeta` and `ColumnMeta` carry independent LWW timestamps for
**position** and **deletion** (and `ColumnMeta` adds one for the
**definition** blob — name / type / validators). This is what lets
"reorder a row" and "delete the row" be resolved by different peers
without colliding.

`TOp` = `TableOpBatch` with 11 polymorphic variants:

| Op | Effect |
|---|---|
| `InsertRowOp` / `MoveRowOp` / `RemoveRowOp` / `RestoreRowOp` | Row lifecycle. |
| `InsertColumnOp` / `MoveColumnOp` / `RemoveColumnOp` / `RestoreColumnOp` | Column lifecycle. |
| `UpdateColumnDefinitionOp` | Update column metadata (name, type, validators). |
| `SetCellOp` / `ClearCellOp` | Cell lifecycle. |

## Worked example

```csharp
var engine = new TableCrdtEngine();

var setup = TableOpBatch.Create(
    new InsertRowOp("R1", "m",  1, "p"),
    new InsertRowOp("R2", "n",  2, "p"),
    new InsertColumnOp("C1", "m", JsonObject("{\"name\":\"Title\"}"), 3, "p"),
    new SetCellOp("R1", "C1", JsonString("Hello"), 4, "p"),
    new SetCellOp("R2", "C1", JsonString("World"), 5, "p"));

var state = engine.Apply(new TableDocument(), setup);
```

## Sticky tombstones

`Remove*` does **not** delete the entry; it flips `IsDeleted = true` and
stores the deletion timestamp. The cell registers for that row / column
stay in the store so concurrent edits don't get silently lost — a
front-end filters them out at render time.

This resolves the classic conflict:

```
t=100 peer A: SetCell(R1, C1, "Hello")
t=200 peer B: RemoveColumn(C1)
```

After both ops: `C1.IsDeleted == true`, the cell `(R1, C1)` is still in
`Cells` (so it's not lost), but UI doesn't render it. If a later peer
calls `RestoreColumnOp(C1, ts > 200)`, the cell becomes visible again with
its original value.

## Sibling ordering

Rows and columns are ordered by their `Position` string — same
fractional-indexing convention as [Tree CRDT](tree-crdt.md). Reuse
`OpStream.Server.Engine.Common.FractionalIndex.Between(left, right)`.

## Typed wrapper

```csharp
public record TaskRow(string Title, DateTime DueDate, bool Done);

var typed = new TableCrdtEngine<TaskRow>();
var op = typed.BuildSetCell("R1", "C1", new TaskRow("Ship docs", DateTime.UtcNow, false),
                             timestamp: 100, peerId: "peer1");
```

Internally the wrapper delegates to the untyped core, so storage and
transport stay uniform.

## Registration

```csharp
services.AddOpStream()
    .AddEngine<TableDocument, TableOpBatch, TableCrdtEngine>("table");
```

## Undo / redo

`TableCrdtEngine` overrides `RestampToWin` so a cached inverse always
beats the current LWW winners. Fully compatible with
[`UndoRedoEngine`](undo-redo.md).

## Limitations

- **No nested structures inside a cell.** The cell value is an opaque
  `JsonElement`; if you need a nested CRDT (e.g. a rich-text editor *inside*
  a cell), compose engines: one Table CRDT for grid layout + per-cell
  Rich-Text or Tree CRDT documents addressed by their `CellAddress`.
- **Tombstones are sticky.** Hard deletion can be implemented as a
  scheduled cleanup task running outside the engine when all peers have
  observed the tombstone.
