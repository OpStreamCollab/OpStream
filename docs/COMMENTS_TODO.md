# Comments / Anchors — Deferred Work

The first iteration of the comments + anchors subsystem (see [the design proposal](../README.md)) is now in. This file tracks what was **intentionally left out** so the next contributor (or future me) can pick up cleanly.

Status legend: 🔴 not started · 🟡 partial · 🟢 done.

---

## What landed (🟢)

- **Data shapes** — `Anchor`, `Comment`, `AnchorUpdate`, `NewCommentCmd` (`src/OpStream.Server/Comments/`).
- **`IPostApplyHook<TOp>`** — invoked inside the session lock between persist and broadcast; explicit return value (no `AsyncLocal`). Re-runs on `RehydrateOpAsync` for recovery.
- **`OpAppliedBackplanePayload.AnchorUpdates`** — optional field on the backplane payload (backward-compatible, no protocol bump).
- **`IAnchorEngine<TOp>` + `TextAnchorEngine`** — covers `TextOp` (plain text OT engine).
- **`ICommentStore` + `MemoryCommentStore`** — default in-memory implementation.
- **`CommentRouter`** — owner-routed mutations via `IBackplaneRequestExtension`; `ListOpenAsync` resolves locally. Reuses `DocumentAccess.CanComment` (no separate `ICommentAuthorizer`).
- **`CommentAnchorRebaseHook<TOp>`** — open-generic hook, becomes no-op when no `IAnchorEngine<TOp>` is registered.
- **SignalR transport** — `CreateComment`, `EditComment`, `ResolveComment`, `DeleteComment`, `ListOpenComments` hub methods + `ReceiveCommentCreated/Updated/Deleted` client events through `SignalRBackplaneRelay`.
- **Atomic anchor at create time** — `IDocumentSession.ExecuteUnderLockAsync` lets the `CommentRouter` snapshot `CurrentRevision` under the session lock when creating a comment on an active session.
- **Tests** — `TextAnchorEngineTests`, `MemoryCommentStoreTests`, `CommentAnchorRebaseHookTests` (11 new tests; all 95 pass).

---

## Deferred (🔴)

### 1. `CompactWithAnchorsService` — **highest priority**

Without this, calling `IDocumentStore.CompactAsync` can leave open comments unable to recover their anchors (their `AnchoredAtRevision` ends up below the compaction floor, so the op log can no longer be replayed for rebase).

**Plan (from the design proposal §I):**
1. Hook the management `CompactDocument` path (`DatabaseCommandRouter.CompactDocumentAsync`) to call a new `CompactWithAnchorsService.CompactAsync(docId, upToRevision, engineType)`.
2. The service:
   - Loads open root anchors (`ICommentStore.LoadOpenAsync`).
   - Streams ops `[min(AnchoredAtRevision) … upToRevision]` from `IDocumentStore`.
   - Applies `IAnchorEngine<TOp>.Rebase` to each anchor for each op (resolved by `engineType`).
   - Calls `ICommentStore.UpdateAnchorsAsync(docId, upToRevision, batch)`.
   - **Then** calls `IDocumentStore.CompactAsync`.
3. Telemetry span: `opstream.comments.compact_rebase` with tag `comments.rebased_count`.

The `engineType` lookup needs a registry (engine-type-string → `IAnchorEngine<TOp>`). Easiest: add a tiny `IAnchorEngineRegistry` keyed on the same discriminator strings used by `AddEngine<TDoc, TOp, TEngine>(documentType)`.

### 2. Additional `IAnchorEngine<TOp>` implementations (🔴)

Per the proposal §J reserved kinds:

- **`RichTextAnchorEngine : IAnchorEngine<RichTextOp>`** — same `text` anchor shape, applied over the Delta's flat-text projection. Reference: `src/OpStream.Server/Engine/RichText/RichTextOp.cs`.
- **`JsonPathAnchorEngine : IAnchorEngine<JsonOpBatch>`** — `Anchor.Data = { "path": "root.users[3].name" }`. Marks `Orphaned` when the path (or any ancestor) is hit by a winning `DeletePropertyOp` under LWW. Reference: `src/OpStream.Server/Engine/Json/`.

Engines explicitly **out of scope** for the comments subsystem (no anchors yet): `TableOpBatch`, `FormOpBatch`, `TreeOpBatch`. The rebase hook is already a no-op for these because there is no registered `IAnchorEngine<TOp>`.

### 3. Persistent `ICommentStore` backends (🔴)

`MemoryCommentStore` is the only implementation today. Comments are **lost on process restart** even when ops are persisted. Required:

- `EfCoreCommentStore<TContext>` in `src/OpStream.Server.Storage.EntityFrameworkCore/` — entity `CommentEntity` with index on `(DocumentId, ParentCommentId, ResolvedAt IS NULL)`. Use a single transaction in `AddAsync` + `UpdateAnchorsAsync` to keep op/anchor writes atomic (EF Core's `SaveChangesAsync` is sufficient).
- `MongoCommentStore` in `src/OpStream.Server.Storage.MongoDB/` — single collection `comments`, anchored regex over `DocumentId`. **Atomicity caveat:** Mongo without a replica set has no multi-collection transactions; documented behaviour is "recovery by replay" via `RehydrateOpAsync` → `CommentAnchorRebaseHook` (already wired up).
- `RedisCommentStore` in `src/OpStream.Server.Storage.Redis/` — HASH `comments:{docId}` keyed by `commentId`, JSON-encoded `Comment` as value. Use a Lua script for `UpdateAnchorsAsync` to keep the batch atomic.

For each new backend, add `Use*CommentStorage()` extensions following the existing `UseRedisStorage()` etc. pattern.

### 4. WebSockets transport surface (🔴)

Mirror the `WebSocketManagementTransport` we already shipped (`src/OpStream.Server.Transports.WebSockets/WebSocketManagementTransport.cs`):

- New `WebSocketCommentsTransport` exposing the five hub methods over a JSON envelope (`{ correlationId, command, ... }`).
- Server-side broadcast: extend `WebSocketBackplaneRelay` to forward `CommentCreated/Updated/Deleted` to the per-doc connection list.
- New `MapOpStreamWebSocketsComments(pattern = "/comments-ws")` extension.

### 5. gRPC transport surface (🔴)

Mirror `gRPCManagementTransport`:

- Add a new `OpStreamCommentsService` to `src/Protos/opstream.proto` with unary RPCs (`CreateComment`, `EditComment`, `ResolveComment`, `DeleteComment`, `ListOpenComments`) and message types (`CommentProto`, `AnchorProto`, `NewCommentCmdProto`, …).
- Implement `gRPCCommentsTransport` and `MapOpStreamGrpcComments`.
- Server-streaming RPC for `SubscribeComments` to push `ReceiveCommentCreated/Updated/Deleted` to clients (gRPC version of the broadcast).
- Re-run `protoc` to regenerate stubs (see the manual command in the gRPC management work — `Grpc.Tools` 2.62.0 still has the `\windows_x64\protoc.exe` path bug on Windows).

### 6. Cache invalidation across non-owner nodes (🟡 partial)

Today every `ListOpenAsync` on a non-owner node hits the shared `ICommentStore` directly. The design (§G) calls for **apply-delta** local caches keyed by `docId` that get updated via the existing `CommentCreated/Updated/Deleted` backplane fan-out (which already carries the full `Comment`).

Tracked here because the design is set; the implementation is just deferred until the persistent backends land — premature otherwise.

### 7. Per-comment authorization (🟡 explicitly avoided)

Currently every comment-touching call goes through `DocumentAccess.CanComment` from `IDocumentAuthorizer`. Decisions such as "only the author can edit", "only admins can delete other users' comments" must live in the host's `IDocumentAuthorizer` implementation for now.

If the host needs richer per-comment policy than `CanComment`, the agreed path forward is a separate `ICommentAuthorizer` interface, but **only when a concrete use case arrives** (proposal §E — YAGNI).

### 8. Telemetry (🔴)

The design (§H) lists:

- Span `opstream.comments.rebase` as child of `opstream.session.apply_op` with tags `comments.affected`, `comments.orphaned`.
- Histogram `opstream.comments.rebase_latency_ms`.
- Counter `opstream.comments.orphaned_total`.
- Span `opstream.comments.compact_rebase` (paired with §1 above) with tag `comments.rebased_count`.

Currently `CommentAnchorRebaseHook` produces no telemetry. Add these alongside the next round of changes.

### 9. JSON anchor — sub-string mode (explicitly rejected, documented)

Anchors inside a string value of a JSON CRDT document (e.g. "comment on characters 3–8 of `users[3].name`") are **not** supported and **will not be added** to `JsonCrdtEngine`. The agreed pattern is to model that field as a separate `TextDocument` linked via an `attachmentId` — see proposal Risk §5.

---

## Risk-watch (re-stated for the next contributor)

- **Lock contention** (Risk 1): the rebase loop runs inside the session lock. For docs with >100 open root comments, measure `opstream.comments.rebase_latency_ms` (once §8 lands) before adding parallelism. The cost of each rebase is O(components-in-op), not O(comments × doc-length).
- **Mongo atomicity** (Risk 2): see §3. Recovery via `RehydrateOpAsync` is the documented contract — verify it works against a Mongo install before pushing to production.
- **`RestrictedRegions`** is still a stub on `DocumentAccess`. Do not build comment-region gating on top of it until it is implemented.

---

## Implementation order (suggested)

1. **§1 (compact rebase)** — without it, every persistent-backend deployment risks orphaned anchors.
2. **§3 EF Core `ICommentStore`** — unlocks most production use cases.
3. **§2 RichText anchor engine** — high user-facing value with the same shape as text.
4. **§8 telemetry**.
5. **§3 Mongo + Redis comment stores**.
6. **§4 WebSockets transport** then **§5 gRPC transport**.
7. **§2 JSON path anchor engine**.
8. **§6 non-owner cache deltas** (after §3 lands).
