# JSON CRDT Engine

LWW (last-writer-wins) CRDT for free-form JSON documents indexed by
**dotted paths**. Best when your document is a tree of named keys whose
shape is mostly stable.

## When to use

- Configuration trees, feature flags, free-form metadata.
- Document headers (title, tags, owner) attached to a richer body.
- Anything modeled as `{ "user.name": "Alice", "user.age": 33, ... }`.

For hierarchical block-style data with reordering, use
[Tree CRDT](tree-crdt.md). For flat name-value pairs, the lighter
[Form OT](form-ot.md) is usually a better fit.

## Types

`TDoc` = `Json_Document` — `Dictionary<path, CrdtRegister>` where each
register stores `(Value, Timestamp, PeerId)`.

`TOp` = `JsonOpBatch` — a bundle of `JsonOp` polymorphic variants:

| Op | Effect |
|---|---|
| `SetPropertyOp(path, value, ts, peerId)` | LWW assign at `path`. |
| `DeletePropertyOp(path, ts, peerId)` | LWW tombstone at `path` (JSON null register). |

Paths are **opaque strings** — the engine uses them as flat keys. A
convention like `"root.user.name"` is fine, but the engine doesn't parse
hierarchy. For real hierarchy with `Move`, use Tree CRDT.

## Worked example

```csharp
var batch = new JsonOpBatch(new[]
{
    new SetPropertyOp("user.name",  JsonString("Alice"),  100, "peer1"),
    new SetPropertyOp("user.age",   JsonNumber(33),       100, "peer1"),
});

var newState = engine.Apply(state, batch);
```

When two peers write to the same path concurrently, the LWW resolution
runs **per-op**: higher timestamp wins, ties broken by ordinal `PeerId`
compare. This is deterministic across replicas regardless of delivery order.

## Registration

```csharp
services.AddOpStream()
    .AddEngine<Json_Document, JsonOpBatch, JsonCrdtEngine>("json");
```

## Undo / redo and `RestampToWin`

`JsonCrdtEngine` overrides `RestampToWin(op, currentState)` so that a
cached inverse always beats any concurrent LWW winner that landed since
record-time. This is what makes
[`UndoRedoEngine<Json_Document, JsonOpBatch>`](undo-redo.md) produce
visible undoes even under heavy concurrency.

## When **not** to use

- **Lists you need to reorder.** Paths are stable, but moving an item
  between paths is "delete + re-create" — you lose history continuity
  and concurrency semantics get awkward.
- **Block-tree shapes** (Notion / outliners). Reach for
  [Tree CRDT](tree-crdt.md) instead.
- **Spreadsheet shapes.** Use [Table CRDT](table-crdt.md).
