# Technical Design Proposal — Named Documents, Versioning & Branching

- **Status:** Draft / for discussion
- **Author:** (proposal)
- **Date:** 2026-05-29
- **Scope:** Server-side document model. Adds human-readable document names, immutable versions (tags), and Git-style branching/merging on top of OpStream's existing op-log + snapshot + history infrastructure.

---

## 1. Motivation

Today a document is addressed by an opaque `documentId` string. There is no first-class notion of:

- **A human name** that is stable while the underlying data evolves (rename, re-home, alias).
- **A version / tag** — an immutable, named pointer to a past state ("v1.0", "approved-2026-Q1") that you can always read back.
- **A branch** — a divergent line of edits that shares history up to a fork point and can later be merged back.

These are common requirements for content tools (drafts vs. published, proposal variants, "save as new version", review workflows). The good news: OpStream already has the hard primitives. This proposal is mostly **naming, ref-tracking, and a merge driver** — not a new CRDT.

### Goals

1. Address documents by a stable **name** decoupled from physical storage id.
2. **Versions (tags):** create an immutable named pointer to a revision; read the document exactly as it was.
3. **Branches:** fork a named document at a revision into an independent editable line; list, read, and **merge** branches.
4. Reuse existing storage providers (EF Core / Mongo / Redis / Memory) with additive schema only.
5. Stay multitenant-safe (every name/ref globalized through `IDocumentIdGlobalizer`).

### Non-goals

- A full Git CLI/semantics (cherry-pick, rebase-interactive, submodules). We expose fork/merge/tag only.
- Cross-document-type merges. A branch always shares the engine type of its parent.
- Client-side conflict UI. We define the server merge contract; UX is per-app.

---

## 2. Background — primitives we already have

The design leans entirely on existing contracts so it can ship incrementally.

| Primitive | Where | What it gives us |
|---|---|---|
| Immutable op log + snapshot per `documentId` | `IDocumentStore` (`AppendOpAsync`, `LoadSnapshotAsync`, `StreamOpsAsync`, `CompactAsync`) | The substance a branch/version points at. |
| Cold history with **named** snapshots & milestones | `IHistoryStore` (`WriteHistorySnapshotAsync(..., name)`, `GetMilestonesAsync`, `LoadHistorySnapshotAsync(maxRevision)`, `StreamHistoryOpsAsync(from,to)`) | Tags are essentially named milestones; time-travel reads already exist. |
| State reconstruction & range composition | `HistoryManager.ReconstructStateAtRevisionAsync`, `ComposeRangeAsync` (the "giga-op") | Read any past state; collapse a branch's edits into one op for merge/diff. |
| Merge math | `IOpEngine.Transform(incoming, existing, priority)`, `Compose`, `Invert`, `IsNoOp`, `RestampToWin` | The 3-way merge driver — transform a branch's ops against the base's concurrent ops. |
| Id namespacing | `IDocumentIdGlobalizer` (`tenant :#: localId`), `EnumerateAsync(tenantPrefix, …)` | A place to hang a structured naming convention without breaking tenancy. |
| Management surface | `IDocumentStore.EnumerateAsync / GetInfoAsync / DeleteAsync` | List/inspect/GC branches & versions. |

**Key realization:** a *branch* is just another op log that was seeded from a parent's state at a fork revision. A *version* is just a named, frozen revision pointer. We don't need new storage engines — we need a **ref registry** and a **merge service**.

---

## 3. Conceptual model

Three new concepts, layered on top of the physical `documentId` (the op-log key).

```
DocumentName  ──(has many)──>  Branch  ──(points at)──>  physical documentId (op log)
                                  │
                                  ├── forkParent: BranchRef + forkRevision   (null for the root branch)
                                  └── tags: Version[]  (name + frozen revision)
```

- **Physical document** — unchanged. An op log + snapshots under a `documentId`. The unit the engine and transports already operate on.
- **DocumentName** — a stable, user-facing identifier (e.g. `"customer-profile"`). Resolves to a *default branch* (like `main`).
- **Branch** — a named line of history with its **own physical `documentId`**. Carries a `forkParent` (which branch) and `forkRevision` (where it diverged). The root branch has no parent.
- **Version (tag)** — an immutable `(name → revision)` pointer within a branch. Backed by a named history milestone/snapshot so the exact bytes are always recoverable even after compaction.

### 3.1 Naming / ref scheme

We keep the physical id opaque but give it structure for refs. Proposed canonical *local* id form (before globalization):

```
   <name>@<branchId>          e.g.  customer-profile@main
                                     customer-profile@draft-x9f2
```

- `<name>` is the user-facing DocumentName.
- `<branchId>` is a slug (`main` for root; generated slug or user label for forks).
- A `Version` is **not** a separate physical id; it's a `(branchId, revision, tagName)` row. Reading a tag = reconstruct-at-revision on the branch's op log.

`IDocumentIdGlobalizer` still wraps the whole thing: `tenant :#: customer-profile@main`. Branch enumeration for a tenant uses the existing `EnumerateAsync(tenantPrefix, …)` + a `name@` filter.

> Migration note: existing documents (`documentId = "customer-profile"`) are treated as `name="customer-profile", branch="main"`. We can lazily adopt the `@main` suffix on first ref-aware access, or run a one-time backfill. Backward compatibility is preserved by a resolver that falls back to the raw id when no ref row exists.

---

## 4. Data model (new ref registry)

A small, provider-agnostic store. Mirrors how `ICommentStore` was added — one interface, per-backend implementations, default `NotSupportedException` so existing providers keep compiling.

```csharp
public interface IDocumentRefStore
{
    // Names
    Task<DocumentNameInfo?> GetNameAsync(string name, CancellationToken ct = default);
    IAsyncEnumerable<DocumentNameInfo> EnumerateNamesAsync(string tenantPrefix, CancellationToken ct = default);

    // Branches
    Task<BranchRef?> GetBranchAsync(string name, string branchId, CancellationToken ct = default);
    IAsyncEnumerable<BranchRef> EnumerateBranchesAsync(string name, CancellationToken ct = default);
    Task CreateBranchAsync(BranchRef branch, CancellationToken ct = default);   // metadata only
    Task DeleteBranchAsync(string name, string branchId, CancellationToken ct = default);

    // Versions / tags (immutable)
    Task CreateVersionAsync(VersionRef version, CancellationToken ct = default);
    Task<VersionRef?> GetVersionAsync(string name, string branchId, string tag, CancellationToken ct = default);
    IAsyncEnumerable<VersionRef> EnumerateVersionsAsync(string name, string branchId, CancellationToken ct = default);
}

public record DocumentNameInfo(string Name, string DefaultBranchId, string EngineType, DateTimeOffset CreatedAt);

public record BranchRef(
    string Name,
    string BranchId,
    string PhysicalDocumentId,        // op-log key, globalized downstream
    string? ForkParentBranchId,       // null for root
    long   ForkRevision,              // revision of parent at fork time (0 for root)
    DateTimeOffset CreatedAt,
    bool   IsReadOnly);               // e.g. an archived branch

public record VersionRef(
    string Name,
    string BranchId,
    string Tag,                       // "v1.0", "approved"
    long   Revision,                  // frozen revision on that branch
    string HistorySnapshotName,       // backing named milestone in IHistoryStore
    DateTimeOffset CreatedAt);
```

EF Core gets two tables (`DocumentBranches`, `DocumentVersions`) + a thin `DocumentNames` table; Mongo gets two collections; Redis gets hash/sorted-set keys. These are tiny metadata rows — no op payloads live here.

---

## 5. Operations

All operations resolve the name/branch → physical `documentId` via `IDocumentRefStore`, then delegate to the existing store/engine/history machinery. New work is concentrated in a `DocumentVersioningService` and a `DocumentMergeService<TDoc,TOp>`.

### 5.1 Create version (tag) — *cheap, no data copy*

Tagging is essentially what `IHistoryStore.WriteHistorySnapshotAsync(documentId, snapshot, name)` already does, plus a `VersionRef` row.

```
Tag(name, branch, tag):
  rev      = current revision of branch's physical doc
  snapshot = HistoryManager.ReconstructStateAtRevisionAsync(physId, rev)  // or reuse latest snapshot
  IHistoryStore.WriteHistorySnapshotAsync(physId, snapshot, name: $"tag/{tag}")
  IDocumentRefStore.CreateVersionAsync(new VersionRef(name, branch, tag, rev, $"tag/{tag}", now))
```

Reading a tag is `ReconstructStateAtRevisionAsync(physId, version.Revision)` — already implemented. Compaction safety: because the tag writes a **named** history snapshot, `PurgeUpToAsync`/`CompactAsync` must refuse to purge below any pinned tag revision (see §7).

### 5.2 Create branch — *copy-on-write fork*

A fork creates a new physical op log seeded from the parent's state at `forkRevision`, plus a `BranchRef`.

```
Branch(name, fromBranch, atRev?, newBranchId):
  forkRev  = atRev ?? current revision of fromBranch
  state    = HistoryManager.ReconstructStateAtRevisionAsync(fromPhysId, forkRev)
  newPhysId = globalize($"{name}@{newBranchId}")
  IDocumentStore.WriteSnapshotAsync(newPhysId, new DocumentSnapshot(0, now, serialize(state)))
  IDocumentRefStore.CreateBranchAsync(new BranchRef(name, newBranchId, newPhysId,
                                       fromBranch, forkRev, now, IsReadOnly:false))
```

The new branch starts at **revision 0 = the forked state** (a clean base snapshot). Its op log is empty and grows independently. We deliberately do **not** copy the parent's op history into the child — the fork snapshot is the child's genesis. The lineage (`ForkParentBranchId`, `ForkRevision`) is retained in metadata so merge can find the common ancestor.

> Cost: one state reconstruction + one snapshot write. O(size of document), not O(history length). Cheap and provider-portable.

### 5.3 List / read

- List branches: `EnumerateBranchesAsync(name)`.
- List versions: `EnumerateVersionsAsync(name, branch)`.
- Read a branch live: join the branch's physical `documentId` exactly as today (transports unchanged).
- Read a version: `ReconstructStateAtRevisionAsync(physId, version.Revision)` → returned as a read-only snapshot.

### 5.4 Diff

`HistoryManager.ComposeRangeAsync(physId, fromRev, toRev)` already produces a single "giga-op" describing the net change of a range. Diffing two versions on the same branch is `ComposeRange(from, to)`. Diffing across branches reuses the merge ancestor logic (§6).

### 5.5 Merge — the only genuinely new algorithm

See §6.

---

## 6. Merge algorithm (3-way, engine-driven)

Merging branch **B** into branch **A** is a 3-way merge with the fork point as the common ancestor. We never need a bespoke diff: OT/CRDT `Transform` is exactly the operator that makes B's intent valid on top of A's concurrent edits.

```
Merge(targetBranch A, sourceBranch B):
  base   = B.ForkRevision on the parent (== the state both started from)
  # 1. Collect each side's ops since the fork.
  opsA   = HistoryManager ops on A's physId   since A's genesis        # A diverged
  opsB   = HistoryManager ops on B's physId   since B's genesis
  # 2. Transform B's ops against A's concurrent ops so they apply on A's tip.
  rebased = []
  for opB in opsB:
      cur = opB
      for opA in opsA:
          cur = engine.Transform(cur, opA, TransformPriority.ExistingWins)   # A (target) wins ties
          if cur is null or engine.IsNoOp(cur): break
      if cur is not null and not engine.IsNoOp(cur):
          rebased.Append(engine.RestampToWin(cur, A.tipState))               # ensure LWW engines accept it
  # 3. Apply rebased ops to A as a normal op batch (goes through the live session/router).
  for op in rebased:
      router.SubmitOp(A.physId, serialize(op))     # same path as a remote client edit
  # 4. Record the merge as a milestone for lineage/audit.
  IHistoryStore.WriteHistorySnapshotAsync(A.physId, snapshotOfA, name: $"merge/{B.BranchId}")
```

### 6.1 Conflict semantics depend on the engine — *for free*

Because the merge is expressed in terms of `Transform`, each engine's existing convergence rules decide conflicts. No new conflict code per engine:

- **Form / JSON (LWW)** — last writer by `(Timestamp, PeerId)` wins. `TransformPriority.ExistingWins` + `RestampToWin` makes the policy explicit ("target branch wins ties on merge"); flip the priority for "source wins". Field-level, so non-overlapping fields merge cleanly.
- **Text / Rich-text (OT)** — positions are transformed so both sides' inserts/deletes survive; this is the canonical OT merge. No data loss, deterministic order via priority.
- **Tree / Table (CRDT)** — already commutative; `Transform` is largely identity and the merge is a replay. Move-cycle/tombstone rules are the engine's, unchanged.

This is the design's biggest leverage point: **merge is a thin driver over `IOpEngine`, so it works for every current and future engine type that honors the contract.**

### 6.2 Conflict reporting

For UX, the merge service can emit a `MergeReport` listing ops that were nullified (`Transform` returned null/no-op) or that lost an LWW tie. This is observational — the merge itself is always convergent. Apps that want manual resolution can run merge in **dry-run** mode (compute `rebased` + report, don't submit) and let a user pick before committing.

### 6.3 Anchored comments

The Comments subsystem already rebases anchors via `IAnchorEngineRegistry` / `CompactWithAnchorsService`. Merge should run the same anchor-rebase pass over the `rebased` op stream so comments on the source branch land at correct positions on the target. This reuses existing code (`CommentAnchorRebaseHook`).

---

## 7. Storage & lifecycle implications

- **Compaction safety:** `CompactAsync` / `PurgeUpToAsync` must not purge below (a) any branch's `ForkRevision` that still has live children, or (b) any pinned `VersionRef.Revision`. Add a `GetPinnedRevisionsAsync(documentId)` lookup the router consults before compacting. Tags already write named history snapshots, so the *bytes* survive purge; this guard just protects the op range needed for diffs/merges.
- **Branch deletion / GC:** deleting a branch removes its `BranchRef` + physical op log (`IDocumentStore.DeleteAsync`, `IHistoryStore.DeleteAsync`). Refuse (or cascade with a flag) if it's the fork parent of a live branch.
- **Provider rollout:** ship `IDocumentRefStore` for EF Core first (covers SQL Server/Postgres/MySQL/SQLite via the shared `OpStreamDbContext`), then Mongo and Redis. Memory store gets an in-proc dict for tests. Providers without it throw `NotSupportedException` — versioning endpoints simply stay disabled, exactly like the comment-store rollout.
- **Migrations:** additive tables only; reuse the existing `MigrationApplicator` / `MigrationHostedService` pipeline.

---

## 8. Multitenancy

Every name, branch id, and physical id flows through `IDocumentIdGlobalizer` before hitting a store, so refs are tenant-scoped automatically. `EnumerateNamesAsync` / `EnumerateBranchesAsync` take a tenant prefix and reuse the existing `EnumerateAsync(tenantPrefix, …)` fan-out. The `:#:` separator constraint (already TODO'd in `TenantAwareDocumentIdGlobalizer`) must be extended to also reject `@` in names/branch ids to keep the local-id grammar unambiguous.

---

## 9. Transport / API surface

Versioning is a **management/control-plane** concern, not a hot-path edit. It does not belong in the per-op SignalR/WebSocket/gRPC message loop. Proposal:

- Add a management facade `IDocumentVersioning` (create name, tag, branch, list, merge, dry-run merge) exposed over the existing management transport (the same place `EnumerateAsync`/`DeleteAsync` are surfaced).
- Live editing of a branch is **unchanged** — clients `ConnectAndJoinAsync(physicalDocumentId, engineType)` where `physicalDocumentId = name@branch`. No wire-format change for the collaborative path.
- Optionally, a client helper resolves `(name, branch)` → physical id so apps never hand-build `name@branch` strings.

---

## 10. Phasing

1. **Refs + names (no branching yet).** `IDocumentRefStore` + EF Core impl; resolver with raw-id fallback; `name@main` adoption. Ships value: stable names + listing.
2. **Versions/tags.** `CreateVersionAsync` + read-a-version (pure reuse of `HistoryManager` + named milestones) + compaction pin guard.
3. **Branching (fork + read).** Copy-on-write fork, list/read branches.
4. **Merge.** `DocumentMergeService<TDoc,TOp>` (dry-run first, then commit), `MergeReport`, anchor rebase integration.
5. **Provider breadth.** Mongo + Redis ref stores; Memory for tests.

Each phase is independently shippable and testable; nothing after phase 1 changes the collaborative edit path.

---

## 11. Risks & open questions

- **Fork cost on huge documents.** Fork reconstructs + snapshots full state. Fine for forms/JSON; large text/tables may want a "lazy fork" that shares the parent snapshot and only writes a child base on first edit. Defer unless measured.
- **Merge of long-diverged branches.** `Transform` cost is O(opsA × opsB). Mitigate by composing each side into a giga-op first (`ComposeRangeAsync`) where the engine supports it, reducing to O(1) transform pairs for LWW/JSON. Text OT still needs per-op transform.
- **LWW tie policy.** Which branch "wins" on merge must be an explicit caller choice (`TransformPriority`), surfaced in the API. Default proposed: target (the branch being merged *into*) wins.
- **Three-way ancestor for cross-lineage merge.** Current design assumes B forked (directly or transitively) from A's lineage. Merging two sibling branches needs walking `ForkParentBranchId` to the lowest common ancestor — straightforward but must be implemented, not assumed.
- **Naming grammar.** Locking `@` and `:#:` out of names/ids; deciding case sensitivity and max length.

---

## 12. Summary

OpStream already stores the *substance* (immutable op logs, snapshots, named history milestones) and already owns the *merge math* (`IOpEngine.Transform/Compose`). This proposal adds a thin **ref registry** (names → branches → physical ids, plus version pointers) and a **merge driver** that reuses the engine contract. The result is Git-style names, immutable versions, and branch/merge that work uniformly across every engine type — with additive-only storage changes and zero impact on the live collaborative edit path.
