# History

Also known as **cold storage**, history is the permanent, append-only record of a
[document's](document.md) lifecycle: every operation and the important state checkpoints
(**milestones**) along the way. Where the hot op log may be [compacted](compaction.md) to
stay small, the history store is the long-term archive.

## What it enables

- **Audit logs** — an immutable trail of who changed what, when.
- **Time-travel reads** — reconstruct and browse the document as it was at any past
  [revision](revision.md).
- **Restoration** — fork a [branch](branching.md) from a historical revision to recover or
  explore a past state.
- **Durable [versions](versioning.md)** — tags write a named history snapshot so they
  survive compaction.

## Milestones

A **milestone** is a named historical snapshot at a particular revision. OpStream writes
them via `IOpHistorySnapshotter`, and version tags create them under the `tag/{tag}` name.
Merges record a `merge/{sourceBranch}` milestone. Milestones are what you list and revisit.

## It is off by default

History is opt-in because not every workload needs an immutable archive. Enable it through
[`OpStreamOptions.History`](../reference/configuration.md#historyoptions-opstreamoptionshistory):

```csharp
services.AddOpStream(options =>
{
    options.History.Enabled = true;                 // master switch
    options.History.SnapshotRevisionInterval = 200; // every N revisions
    // options.History.SnapshotInterval = TimeSpan.FromMinutes(10);
});
```

When disabled, a no-op snapshotter is registered and nothing is archived.

## Hot store vs. cold store

| | Hot store ([Snapshot](../operations/snapshots.md)) | Cold store (History) |
|---|---|---|
| Purpose | Bound op-log replay on reload | Permanent archive / time travel |
| Lifetime | May be compacted/trimmed | Append-only, durable |
| Driven by | `ISnapshotPolicy` | `OpStreamOptions.History` |
| Backed by | `IDocumentStore` | `IHistoryStore` |

Persistence is via `IHistoryStore`, implemented by every storage package.

## See also

- [Snapshot](../operations/snapshots.md) — the hot-store checkpoint mechanism and the
  shared "Snapshots and history" page.
- [Compaction](compaction.md) — what history protects data from.
- [Versioning](versioning.md) — tags backed by history milestones.
