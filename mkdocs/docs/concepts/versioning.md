# Versioning

A **version** (or **tag**) is an immutable, named pointer to a specific
[revision](revision.md) on a [branch](branching.md). It lets you reliably "time-travel" to
a past state and read the document exactly as it was when the version was created — think
`v1.0`, `before-migration`, `approved-draft`.

## How a tag stays readable forever

Creating a version (`CreateVersion`) does two things:

1. Writes a **named [history](history.md) snapshot** (`tag/{tag}`) so the bytes are
   captured in cold storage.
2. Records a **version ref** in the registry pointing the tag at that revision.

Because the snapshot is in the history store, a tag remains readable **even after the
active op log is [compacted](compaction.md)**.

## Tags pin the compaction floor

A version tag also acts as a **retention lock**: compaction and history purges may never
delete below the oldest revision pinned by a tag (`GetMinPinnedRevisionAsync` is the
floor). This guarantees a tagged state can never be silently destroyed.

The flip side: an un-deletable tag would hold history forever, so OpStream provides
`DeleteVersion`, which removes the tag ref (immediately releasing the floor) and optionally
drops the backing snapshot bytes.

## Operations

| Operation | Effect |
|---|---|
| `CreateVersion(name, branch, tag)` | Tag the branch's current revision; write the surviving snapshot. |
| `ListVersions(name, branch)` | Enumerate tags on a branch. |
| `ReadVersionSnapshot(name, branch, tag)` | Read the exact bytes captured at tag time. |
| `DeleteVersion(name, branch, tag)` | Remove the tag and release its compaction pin. |

All of these are part of the control plane and gated by
[`IDatabaseCommandAuthorizer`](../reference/configuration.md#authorization).

## Versions vs. branches

A **branch** is a living, editable line of history. A **version** is a frozen, read-only
bookmark into one. You tag *on* a branch; you fork *from* a branch or a version's revision.

## See also

- [Branching](branching.md) — the editable lines that versions bookmark.
- [History](history.md) — the cold store that keeps tagged snapshots alive.
- [Compaction](compaction.md) — what version pins protect against.
