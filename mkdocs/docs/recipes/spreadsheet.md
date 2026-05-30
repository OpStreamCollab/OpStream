# Recipe: Collaborative spreadsheet

!!! tip "Working HTML + JS sample"
    For a ready-to-run, no-.NET version, see
    [`samples/luckysheet-collab`](https://github.com/OpStreamCollab/OpStream/tree/main/samples/luckysheet-collab):
    the unmodified Luckysheet spreadsheet made collaborative over the JSON engine
    via SignalR. `docker run -p 8080:8080 opstreamcollab/opstream`, then
    `npm install && npm run dev`.

A simplified Airtable / Google Sheets clone backed by
[`TableCrdtEngine`](../engines/table-crdt.md). Concurrent users add and
remove rows, edit cells, and reorder columns without locks.

## Domain model

```csharp
// Each cell stores a typed value
public abstract record CellValue;
public record TextValue(string Text) : CellValue;
public record NumberValue(decimal N)  : CellValue;
public record BoolValue(bool B)       : CellValue;

// Column definition — name, type, validators
public record ColumnDef(string Name, string Type, int? MaxLength = null);
```

## Server

```csharp
using OpStream.Server.Engine.Table;

builder.Services
    .AddOpStream()
    .UsePostgreSql(builder.Configuration.GetConnectionString("OpStream")!)
    .UseAuthorization<MyAuthorizer>()
    .AddEngine<TableDocument, TableOpBatch, TableCrdtEngine>("sheet")
    .AddSignalRTransport();
```

## Client helpers

```csharp
using OpStream.Server.Engine.Common;
using OpStream.Server.Engine.Table;

readonly TableCrdtEngine<CellValue> typed = new();

long NextTs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

string PositionAfter(string? prev, string? next) => FractionalIndex.Between(prev, next);
```

## Inserting a row at the end

```csharp
var lastRow = state.Rows.Values
    .Where(r => !r.IsDeleted)
    .OrderBy(r => r.Position, StringComparer.Ordinal)
    .LastOrDefault();

var batch = TableOpBatch.Create(
    new InsertRowOp(
        RowId:     Guid.NewGuid().ToString("n"),
        Position:  PositionAfter(lastRow?.Position, null),
        Timestamp: NextTs(),
        PeerId:    myPeerId));

await client.SendOpAsync(docId, JsonSerializer.SerializeToUtf8Bytes(batch), baseRevision);
```

## Editing a cell

```csharp
var setOp = typed.BuildSetCell(
    rowId:     rowId,
    columnId:  columnId,
    value:     new TextValue("Hello"),
    timestamp: NextTs(),
    peerId:    myPeerId);

await client.SendOpAsync(docId, JsonSerializer.SerializeToUtf8Bytes(
    new TableOpBatch(new TableOp[] { setOp })),
    baseRevision);
```

If two users edit the same cell concurrently, the higher timestamp
wins; ties are broken by ordinal `PeerId` compare. Both replicas
converge.

## Deleting a column

```csharp
var batch = TableOpBatch.Create(
    new RemoveColumnOp(columnId, NextTs(), myPeerId));
```

The column is **soft-deleted** — its `IsDeleted` flag flips on but
existing cells in that column stay in storage. UI filters them at render
time. If another user concurrently edited a cell in that column, their
write is preserved (just not rendered while the column is tombstoned).

A later `RestoreColumnOp` un-tombstones the column and brings the cell
values back into view.

## Reordering columns

```csharp
var newPos = PositionAfter(siblingLeft.Position, siblingRight.Position);

var batch = TableOpBatch.Create(
    new MoveColumnOp(columnId, newPos, NextTs(), myPeerId));
```

## Updating a column definition

```csharp
var newDef = JsonSerializer.SerializeToElement(
    new ColumnDef("Priority", "select", MaxLength: 20));

var batch = TableOpBatch.Create(
    new UpdateColumnDefinitionOp(columnId, newDef, NextTs(), myPeerId));
```

## Rendering — filter tombstones

```csharp
IEnumerable<RowMeta> VisibleRows(TableDocument doc) =>
    doc.Rows.Values
        .Where(r => !r.IsDeleted)
        .OrderBy(r => r.Position, StringComparer.Ordinal);

IEnumerable<ColumnMeta> VisibleColumns(TableDocument doc) =>
    doc.Columns.Values
        .Where(c => !c.IsDeleted)
        .OrderBy(c => c.Position, StringComparer.Ordinal);

CellValue? ReadCell(TableDocument doc, string rowId, string columnId) =>
    doc.Cells.TryGetValue(new CellAddress(rowId, columnId), out var reg)
        ? typed.ReadCell(reg)
        : null;
```

## Undo / Redo

`TableCrdtEngine` overrides `RestampToWin`, so `UndoRedoEngine<TableDocument, TableOpBatch>`
produces undoes that stay visible under heavy concurrent editing. See
[Undo / Redo](../engines/undo-redo.md).

## Per-cell awareness

Show which cell each peer is editing:

```csharp
await client.SendAwarenessAsync(docId,
    JsonSerializer.SerializeToElement(new
    {
        focused = new { rowId, columnId },
        name = currentUserName,
        color = currentUserColor,
    }));
```

Render a small highlighted border in each peer's color on the
referenced cell.
