# Branching

A **branch** is a named, independent line of edits derived from a parent
[document's](document.md) state at a specific [revision](revision.md) — the **fork point**.
Each branch has its own physical op log and grows independently, exactly like a Git branch.
OpStream supports Git-style branching and [merging](merging.md) back together.

## Model

Branches are tracked in a **ref registry** (`IDocumentRefStore`), separate from the op log
itself. A branch ref records:

| Field | Meaning |
|---|---|
| **Name** | The logical document name the branch belongs to. |
| **Branch id** | The branch's own identifier (e.g. `main`, `experiment`). |
| **Physical document id** | The actual storage key, by convention `tenant:#:name@branchId`. |
| **Fork parent / fork revision** | Which branch it was forked from, and at which revision (the merge base). |
| **Read-only flag** | Whether the branch accepts new edits. |

Every registered name starts with a root branch (default `main`).

## Forking

`ForkBranch` creates a new branch from an existing one:

- **At HEAD** (no revision specified) — copies the source branch's latest
  [snapshot](../operations/snapshots.md) as the new branch's revision-0 state. Compact the
  source first if you want an exact current-state fork.
- **At a historical revision** — copies from the [history](history.md) store's snapshot at
  or before that revision, letting you branch from the past.

The new branch is fully independent: edits on it never touch the parent, and vice versa.

## Safety

- A branch **cannot be deleted while it has child forks** — you must delete the children
  first (or cascade).
- Branch mutations (fork, delete) are part of the **management/versioning control plane**
  and are gated by [`IDatabaseCommandAuthorizer`](../reference/configuration.md#authorization);
  destructive ones evict any live [session](session.md) on the owning node before touching
  storage.

## Enabling it

Branching needs a persistent ref store and (for merge) a registered merge driver:

```csharp
services.AddOpStream()
    .UseEfCoreStorage<MyDbContext>()
    .UseEfCoreVersioningStorage<MyDbContext>()        // ref registry
    .UseVersioningMerge<TextDocument, TextOp>("text"); // per engine type
```

## See also

- [Versioning](versioning.md) — immutable tags pinning specific revisions.
- [Merging](merging.md) — integrating a branch's edits back into another.
- [History](history.md) — the cold store branches can fork from.
