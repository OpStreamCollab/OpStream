# Compaction

**Compaction** keeps a [document's](document.md) hot op log from growing without bound. As
edits accumulate, replaying every [op](../reference/interfaces.md) from genesis to rebuild
state would get slower and slower. Compaction collapses the history up to a chosen
[revision](revision.md) into a [snapshot](../operations/snapshots.md) and discards the ops
below it.

## What it does

1. Ensure a snapshot exists at (or is written for) the target revision.
2. Rebase any open [comment anchors](comments.md#anchors) so they keep pointing at the
   right content — handled by `CompactWithAnchorsService` **before** anything is removed.
3. Trim the op log up to the target revision.

The result: faster session rehydration and less storage, with no loss of current state or
anchor fidelity.

## Compaction vs. purge

| | Compaction | Purge |
|---|---|---|
| Target | Hot op log | Cold [history](history.md) store |
| Goal | Bound replay cost | Reclaim long-term archive space |
| Command | `CompactDocument(upToRevision)` | `PurgeHistory(upToRevision)` |

Both are part of the management control plane and gated by
[`IDatabaseCommandAuthorizer`](../reference/configuration.md#authorization).

## The version pin guard

Compaction and purge are **floored** by [version tags](versioning.md). A tag pins its
revision (`GetMinPinnedRevisionAsync`), and neither operation may delete below the oldest
pinned revision. So a tagged state can never be silently destroyed — to release the floor
you must explicitly `DeleteVersion`.

## Anchors are never lost

Because anchor rebasing runs as the first step of compaction, comments anchored to content
*below* the compaction point are moved onto the snapshot's coordinate space rather than
being orphaned by the trim. See [Comment anchors](comments.md#anchors).

## See also

- [Snapshot](../operations/snapshots.md) — the checkpoint compaction collapses ops into.
- [History](history.md) — the cold store that `PurgeHistory` trims.
- [Versioning](versioning.md) — the pins that floor compaction.
