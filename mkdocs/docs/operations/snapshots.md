# Snapshots and history

Replaying the full op log on every document load doesn't scale. OpStream
periodically captures **snapshots** — compact serialized states — so
sessions can rehydrate quickly and old ops can (optionally) be trimmed.

## Policy

`ISnapshotPolicy` controls when snapshots are taken. The default is
`HybridSnapshotPolicy(opsThreshold: 100, timeThreshold: 5 min)` — a
snapshot is taken after either 100 accepted ops OR 5 minutes since the
last snapshot, whichever comes first.

Override with:

```csharp
services.AddOpStream()
    .UseSnapshotPolicy(new HybridSnapshotPolicy(
        opsThreshold:  50,
        timeThreshold: TimeSpan.FromMinutes(1)));
```

Or implement your own:

```csharp
public sealed class MyPolicy : ISnapshotPolicy
{
    public bool ShouldSnapshot(long opsSinceLast, TimeSpan elapsedSinceLast)
        => opsSinceLast >= 200 || elapsedSinceLast >= TimeSpan.FromMinutes(15);
}

services.AddOpStream().UseSnapshotPolicy(new MyPolicy());
```

## Lifecycle

1. Every accepted op feeds the snapshotter (`IOpSnapshotter.OpAddedAsync`).
2. When the policy says yes, the snapshotter serializes the current state
   via `JsonSerializer` and writes it to `IDocumentStore.SaveSnapshotAsync`.
3. On the next idle close (no peers connected), a final snapshot is taken
   to capture any tail of ops the policy didn't trigger.
4. On document load, the session calls `LoadSnapshotAsync` and replays
   only ops with revision > snapshot.Revision.

## History

A separate `IOpHistorySnapshotter` records longer-term **milestones**
(named revisions) that users can revisit. Enabled via options:

```csharp
services.AddOpStream(opts =>
{
    opts.History.Enabled = true;
    opts.History.MaxMilestonesPerDocument = 50;
});
```

When enabled, the framework keeps the last N milestones per document.
Persistence is via `IHistoryStore` — provided by every storage package.

## Trimming the op log

Snapshots make trimming possible. The semantic is:

> *Once a snapshot at revision R is durable, ops with revision ≤ R can
> be dropped without losing the ability to reconstruct any state ≥ R.*

OpStream doesn't trim automatically — your app picks the policy
(append-only audit log vs. compaction). Provider-specific trim helpers:

- Redis: `XTRIM opstream:ops:{docId} MINID <snapshotRevision>`
- SQL: `DELETE FROM stored_ops WHERE document_id = @id AND revision <= @r`
- MongoDB: `db.ops.deleteMany({ documentId, revision: { $lte: r } })`

Wrap the chosen approach in a background service if you want it
automated.

## See also

- [Storage overview](../storage/index.md) — how snapshots are persisted.
- [Builder API: UseSnapshotPolicy](../reference/builder-api.md#usesnapshotpolicy).
