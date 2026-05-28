# Tree CRDT Engine

CRDT for **hierarchical trees** with a native, conflict-free **Move**
operation. Based on Kleppmann *et al.*'s 2021 move-tree algorithm.

## When to use

- Notion / Outliner-style block trees.
- File-system / folder hierarchies.
- Mind maps, hierarchical TODO lists.
- Any UI where moving a node from one parent to another must survive
  concurrent edits without creating cycles.

## Why not JSON CRDT?

In a JSON CRDT, moving a node from one parent to another is "delete then
re-create" — you lose history continuity, and two peers moving the same
node concurrently to different parents can leave the document in a
nonsense state.

Tree CRDT makes `Move` a **first-class atomic operation**, with cycle
detection and a timestamp-ordered move log that absorbs late-arriving ops
correctly.

## Types

`TDoc` = `TreeDocument`:

| Field | Description |
|---|---|
| `Nodes : Dictionary<id, TreeNode>` | All known nodes, alive or tombstoned (`ParentId == TrashId`). |
| `MoveLog : IReadOnlyList<AppliedMove>` | Every move applied so far, sorted by `(Timestamp asc, PeerId asc)`. |

`TreeNode = (Id, ParentId, Position, Payload)`.

| Special parent id | Meaning |
|---|---|
| `TreeConstants.RootId` (`"__root__"`) | Top-level nodes attach here. |
| `TreeConstants.TrashId` (`"__trash__"`) | Tombstone parent — deletion modelled as a move. |

`TOp` = `TreeOpBatch` wrapping one or more `MoveOp`:

```csharp
new MoveOp(
    NodeId: "block-42",
    NewParentId: "block-7",
    NewPosition: "m",           // fractional index — see below
    NewPayload: payload,        // JsonElement opaque to the engine
    Timestamp: 100,
    PeerId: "peer1");
```

`Move` is the **only** operation — Insert, Delete, Reorder, and Reparent
are all expressed as moves:

| You want to… | Build |
|---|---|
| Create a new node | `MoveOp(newId, parentId, position, payload, …)` |
| Move a node | `MoveOp(existingId, newParentId, newPosition, payload, …)` |
| Delete a node | `MoveOp(id, TreeConstants.TrashId, position, payload, …)` |
| Reorder siblings | `MoveOp(id, sameParentId, newPosition, payload, …)` |

## Ordering siblings — fractional indexing

`Position` is a lexicographically-comparable string. v1 ships
[`FractionalIndex.Between(left, right)`](../reference/builder-api.md#fractionalindex)
in `OpStream.Server.Engine.Common`:

```csharp
using OpStream.Server.Engine.Common;

var first  = FractionalIndex.Between(null, null);   // some mid-range key
var before = FractionalIndex.Between(null, first);  // sorts before `first`
var after  = FractionalIndex.Between(first, null);  // sorts after `first`
var middle = FractionalIndex.Between(before, first);// sorts between them
```

If the boundaries are degenerate (no alphabet-valid intermediate exists,
e.g. `Between("a", "a!")`), the call throws `ArgumentException`. The
caller should react — typically by re-balancing neighbouring positions.

!!! info "Why fractional indexing for now?"
    A full sequence CRDT (RGA / LSEQ) is the long-term plan and would
    eliminate the rare "no key fits between" failure mode. The Position
    field's wire shape is already a string, so a future migration is
    non-breaking.

## Worked example

```csharp
var engine = new TreeCrdtEngine();
var batch = new TreeOpBatch(new TreeOp[]
{
    new MoveOp("A", TreeConstants.RootId, "m", JsonNull, 1, "p"),
    new MoveOp("B", "A",                  "m", JsonNull, 2, "p"),
    new MoveOp("C", "A",                  FractionalIndex.Between("m", null),
               JsonNull, 3, "p"),
});

var state = engine.Apply(new TreeDocument(), batch);
// state.Nodes contains A under __root__, and B+C under A.
```

## Cycle prevention

`MoveOp` is rejected if applying it would make a node its own ancestor.
The check covers:

- Direct cycle (`Move(A, A)` or `Move(A, B)` where `B`'s ancestry includes `A`).
- **Orphan-pointer cycles** during undo / redo replay: if another node
  declares `ParentId = A` while `A` is transiently absent from the map,
  re-inserting `A` is still refused.

The offending move stays in the log as a no-op so all replicas converge
to the same state.

## Typed wrapper

`TreeCrdtEngine<TPayload>` (also in `OpStream.Server.Engine.Tree`) lets
your code work with a domain payload instead of `JsonElement`:

```csharp
public record BlockContent(string Kind, string Text);

var typed = new TreeCrdtEngine<BlockContent>();
var move = typed.BuildMove(
    nodeId: "blk-1",
    newParentId: TreeConstants.RootId,
    newPosition: "m",
    payload: new BlockContent("paragraph", "Hello"),
    timestamp: 100,
    peerId: "peer1");
```

Internally the wrapper delegates to the untyped core, so the wire format
stays uniform across clients.

## Registration

```csharp
services.AddOpStream()
    .AddEngine<TreeDocument, TreeOpBatch, TreeCrdtEngine>("tree");
```

## Undo / redo

Compatible with [`UndoRedoEngine`](undo-redo.md). The move-log absorbs
stale timestamps natively, so the engine inherits the identity default
`RestampToWin` — no override needed.

## Limitations

- Position generation is fractional indexing (see warning above).
- The move log grows with the number of applied ops; snapshot the
  document periodically to truncate the replay cost
  ([Snapshots](../operations/snapshots.md)).
