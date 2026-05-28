# Recipe: Notion-style block tree

A hierarchical document where each block (paragraph, heading, todo,
toggle) has a typed payload, blocks are reorderable by drag-and-drop,
and concurrent moves never produce cycles.

## What we'll use

- [`TreeCrdtEngine<BlockContent>`](../engines/tree-crdt.md) — typed Tree CRDT.
- [`FractionalIndex`](../engines/tree-crdt.md#ordering-siblings-fractional-indexing) for sibling order.
- SignalR transport.
- Storage of choice (Postgres in this example).

## Domain model

```csharp
public record BlockContent(
    string Kind,              // "paragraph" / "heading" / "todo" / "toggle" / ...
    string Text,              // textual content
    string? Annotation = null // type-specific metadata, e.g. todo.checked = "true"
);
```

## Server

```csharp
using OpStream.Server.Engine.Tree;

builder.Services
    .AddOpStream()
    .UsePostgreSql(builder.Configuration.GetConnectionString("OpStream")!)
    .UseAuthorization<MyAuthorizer>()
    .AddEngine<TreeDocument, TreeOpBatch, TreeCrdtEngine>("blocks")
    .AddSignalRTransport();
```

## Client — creating a block

```csharp
using OpStream.Server.Engine.Tree;
using OpStream.Server.Engine.Common;

// "Insert a paragraph block at the end of page-7."
var parent = "page-7";
var lastPosition = blocks.Where(b => b.ParentId == parent)
                         .Select(b => b.Position)
                         .DefaultIfEmpty(null)
                         .Max();
var position = FractionalIndex.Between(lastPosition, null);

var typed = new TreeCrdtEngine<BlockContent>();
var op = typed.BuildMove(
    nodeId:       Guid.NewGuid().ToString("n"),
    newParentId:  parent,
    newPosition:  position,
    payload:      new BlockContent("paragraph", "New block"),
    timestamp:    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    peerId:       myPeerId);

var batch = new TreeOpBatch(new TreeOp[] { op });
await client.SendOpAsync("page-1", JsonSerializer.SerializeToUtf8Bytes(batch), baseRevision);
```

## Drag-and-drop reorder

Drag-and-drop is a `Move` with a new `Position`:

```csharp
var dropPosition = FractionalIndex.Between(previousSiblingPos, nextSiblingPos);

var move = typed.BuildMove(
    nodeId:      draggedBlockId,
    newParentId: targetParentId,
    newPosition: dropPosition,
    payload:     draggedBlock.Payload,
    timestamp:   DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    peerId:      myPeerId);
```

If two users drag the same block to different parents at the same time,
the timestamp-ordered move log resolves it deterministically — the later
move wins, the earlier becomes a no-op for the conflicting bit but stays
in the log so future replicas converge.

## Deleting a block

Modeled as a move to `TreeConstants.TrashId`:

```csharp
var del = typed.BuildMove(
    nodeId:      blockId,
    newParentId: TreeConstants.TrashId,
    newPosition: "",                       // ignored in trash
    payload:     block.Payload,
    timestamp:   DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    peerId:      myPeerId);
```

The block stays in `state.Nodes` (tombstoned) so a concurrent edit to it
isn't silently lost. UI filters anything whose `ParentId == TrashId`.

## Handling "no key fits between"

`FractionalIndex.Between(a, b)` throws `ArgumentException` when no
alphabet-valid intermediate exists — extremely rare in practice, but
real. Catch and rebalance:

```csharp
try
{
    position = FractionalIndex.Between(prev, next);
}
catch (ArgumentException)
{
    // Rebalance: pick a far-apart key — e.g. midway between FAR_LEFT and FAR_RIGHT
    // and re-emit MoveOps for the immediate neighbours.
    position = FractionalIndex.Between(null, null);
    await RebalanceNeighboursAsync(parent);
}
```

A future Sequence CRDT (RGA / LSEQ) eliminates this corner case entirely.

## Rich content per block

If a block needs collaborative rich-text inside it (a paragraph block
holding a Quill / TipTap document):

- Keep the tree CRDT for **structure only** (block tree).
- Open a second OpStream document per block, with document id `blockId`
  and document type `"rich-text"`.

The two documents are independent — different engines, different
sessions, joint editing experience.
