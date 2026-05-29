# Technical Design Proposal — Unified Data Access & Deletion Plane

- **Status:** Draft / for discussion
- **Author:** (proposal)
- **Date:** 2026-05-29
- **Scope:** Server- and client-side management plane. Gives end users (and host applications) a single, authorized, transport-agnostic surface to **read** and **delete** every class of stored artifact OpStream now produces: documents, snapshots, op history, named documents, branches and versions.

---

## 1. Motivation

OpStream has, over the last several phases, grown a rich persistence story:

- **Hot store** (`IDocumentStore`): live op-log + current snapshot.
- **Cold store / history** (`IHistoryStore`): historical snapshots, op ranges, milestones.
- **Versioning** (`IDocumentRefStore` + `VersioningRouter`): names → branches → versions (tags), copy-on-write forks, engine-driven 3-way merge.
- **Compaction**, snapshotting, milestones, tenant isolation.

What is *missing* is a coherent way for a user to **see and remove** all of that. Today the only management surface is:

- `DatabaseCommandRouter` — list/inspect/delete/compact/purge for **documents and history**, fully authorized, exposed over SignalR + WebSockets.
- `VersioningRouter` — names/branches/versions/merge, **with no authorization at all**, exposed over **SignalR only**.

There is **no client-side SDK** for either. A consumer who wants to "list my documents" or "delete this branch" has to hand-craft raw hub invocations, and when they do, the versioning calls bypass every permission check the rest of the system enforces.

This document proposes closing those gaps with one design: a **Unified Data Access & Deletion Plane**.

### Goals

1. **Single authorization model** covering *both* the database/management commands and the versioning commands. One policy seam (`IDatabaseCommandAuthorizer`) decides everything.
2. **Complete read + delete coverage** for every stored artifact, with explicit, documented cascade and safety semantics.
3. **Typed client SDK** (`IOpStreamManagementClient`) parallel to the existing `IOpStreamClient`, with SignalR and WebSocket implementations.
4. **Transport parity** — versioning becomes reachable over WebSockets **and gRPC**, not just SignalR (the database plane already covers all three).
5. Stay **multitenant-safe** (all ids globalized) and **cluster-safe** (mutations routed to the owner node, caches evicted) — exactly as `DatabaseCommandRouter` already does.
6. **Additive and incremental** — no breaking change to the collaboration path or to existing storage providers.

### Non-goals

- A management *UI*. We define the contracts and SDK; the dashboard/app is per-host.
- Soft-delete / trash-bin / undelete. Deletes are physical (discussed as an optional extension in §10).
- Rewriting authorization for the **collaboration** path (`IDocumentAuthorizer` for join/op). This proposal is strictly the *management* plane.
- Quotas, rate limiting, or billing metering (orthogonal; can layer on the same seam later).

---

## 2. Current State — a detailed audit

### 2.1 The two control planes

| Concern | Router | Authorized? | Tenant-globalized? | Owner-routed mutations? | SignalR | WebSockets | gRPC | Client SDK |
|---|---|---|---|---|---|---|---|---|
| Documents / history / snapshots | `DatabaseCommandRouter` | ✅ every method via `IDatabaseCommandAuthorizer` | ✅ | ✅ (delete/compact/purge → owner; purge-tenant fan-out) | ✅ `SignalRManagementTransport` | ✅ `WebSocketManagementTransport` | ✅ `gRPCManagementTransport` (`OpStreamManagementService`) | ❌ |
| Names / branches / versions / merge | `VersioningRouter` | ❌ **none** | ✅ | ❌ (writes storage directly) | ✅ `SignalRVersioningTransport` | ❌ | ❌ | ❌ |

The database/management plane is reachable over **all three** transports; the versioning plane is **SignalR-only**. So transport parity (§7) means adding versioning to *both* WebSockets *and* gRPC.

### 2.2 `DatabaseCommandRouter` — the good template

`src/OpStream.Server/Session/DatabaseCommandRouter.cs` is the reference implementation we want everything to look like:

- Every public method calls `AuthorizeAsync(new DatabaseCommandContext(...))` first and returns `Forbidden<T>()` on a `false`.
- Read commands resolve **locally** against `IDocumentStore` / `IHistoryStore` (storage is shared across nodes).
- Mutating per-document commands (`DeleteDocument`, `CompactDocument`, `PurgeHistory`) go through `RouteToOwnerAsync` → the **owner node** so any live in-memory session is evicted *before* storage is touched, then a `DocumentDeleted` broadcast drops caches cluster-wide.
- `PurgeTenant` fans out an `EvictTenant` broadcast, then bulk-deletes via `DeleteByTenantPrefixAsync`.
- Every returned id is stripped back to a **local** id via `globalizer.ToLocalId(...)`.

Current command set (`DatabaseCommandType` in `src/OpStream.Shared.Abstractions/DatabaseCommands.cs`):

```
ListDocuments, GetDocumentInfo, GetSnapshot, DeleteDocument,
CompactDocument, ListMilestones, PurgeHistory, PurgeTenant
```

### 2.3 `VersioningRouter` — the gaps

`src/OpStream.Server/Versioning/VersioningRouter.cs` follows the same *globalization* conventions, **but**:

1. **No authorization.** None of `RegisterNameAsync`, `ListNamesAsync`, `ListBranchesAsync`, `ForkBranchAsync`, `DeleteBranchAsync`, `CreateVersionAsync`, `ListVersionsAsync`, `ReadVersionSnapshotAsync`, `MergeAsync` consults any authorizer. The XML doc on `SignalRVersioningTransport` even claims "optional authorization" — that is currently aspirational; the seam does not exist. **`DeleteBranchAsync` physically deletes a branch's op log and snapshot with zero permission check.**
2. **No owner routing for mutations.** `ForkBranchAsync` / `DeleteBranchAsync` / `CreateVersionAsync` write storage directly on whatever node received the call. A live editing session on the affected physical document is **not evicted**, so an in-memory session can keep serving / re-persisting state that was just deleted on another node.
3. **`DatabaseCommandType` has no versioning entries**, so even a host that *wanted* to authorize these can't express the policy through the existing seam.
4. **No `DeleteVersion` / tag deletion.** You can `CreateVersionAsync` (writes a named history snapshot + a `VersionRef`) but there is no way to remove a tag. Tags also **pin compaction** (`GetMinPinnedRevisionAsync` is the purge floor), so an un-deletable tag permanently holds history.
5. **WebSocket and gRPC transports are missing.** `WebSocketManagementMessage` / `WebSocketManagementTransport` and `gRPCManagementTransport` (the `OpStreamManagementService` rpc in `src/Protos/opstream.proto`) exist for the database plane, but there is **no versioning equivalent on either** — the `.proto` defines `OpStreamService`, `OpStreamManagementService`, and `OpStreamCommentsService`, but no `OpStreamVersioningService`. Versioning is reachable **only** over SignalR.

### 2.4 Client side

`src/OpStream.Client.Transports/IOpStreamClient.cs` covers only the collaboration path:

```
ConnectAndJoinAsync, SendOpAsync, SendAwarenessAsync,
ListOpenCommentsAsync, CreateCommentAsync, EditCommentAsync,
ResolveCommentAsync, DeleteCommentAsync
```

A `Grep` for `Management|Versioning|DatabaseCommand|DeleteDocument|ListDocuments` across `**/*Client*.cs` returns **nothing**. There is no typed management API on the client at all.

---

## 3. Design Overview

Three coordinated changes:

```
                       ┌─────────────────────────────────────────────┐
                       │      IDatabaseCommandAuthorizer (one seam)    │
                       │  DatabaseCommandType { …docs… + …versioning…} │
                       └───────────────▲─────────────────▲────────────┘
                                       │                  │
                 ┌─────────────────────┴───┐   ┌──────────┴───────────────┐
                 │   DatabaseCommandRouter  │   │     VersioningRouter      │
                 │  (docs / history / snap) │   │ (names/branches/versions) │
                 └───────▲─────────▲────────┘   └─────▲──────────▲─────────┘
                         │                            │
        SignalR / WS / gRPC ManagementTx       SignalR VerTx (+ WS & gRPC VerTx, NEW)
                         │                            │
                 ┌───────┴────────────────────────────┴────────────────────┐
                 │      IOpStreamManagementClient  (NEW typed SDK)          │
                 │      SignalR impl  +  WebSocket impl  +  gRPC impl       │
                 └──────────────────────────────────────────────────────────┘
```

1. **§4 — Unify authorization:** extend `DatabaseCommandType`, give `VersioningRouter` the same `AuthorizeAsync` guard and owner-routing that `DatabaseCommandRouter` has.
2. **§5 — Complete the read+delete taxonomy:** add the missing primitives (`DeleteVersion`, optionally `DeleteName`) so every artifact has both a read and a delete path with defined cascade semantics.
3. **§6 — Ship the client SDK:** `IOpStreamManagementClient` with SignalR, WebSocket, and gRPC implementations; **§7** adds the missing WebSocket *and* gRPC versioning transports.

---

## 4. Unified Authorization Model

### 4.1 Extend the command discriminator

Add versioning operations to `DatabaseCommandType`:

```csharp
public enum DatabaseCommandType
{
    // ── existing: documents & history ──────────────
    ListDocuments,
    GetDocumentInfo,
    GetSnapshot,
    DeleteDocument,
    CompactDocument,
    ListMilestones,
    PurgeHistory,
    PurgeTenant,

    // ── new: versioning control plane ──────────────
    RegisterName,
    ListNames,
    DeleteName,          // new primitive — see §5.3
    ListBranches,
    ForkBranch,
    DeleteBranch,
    CreateVersion,
    ListVersions,
    ReadVersionSnapshot,
    DeleteVersion,       // new primitive — see §5.2
    MergeBranch,         // covers dry-run and commit (distinguish via Args["dryRun"])
}
```

`DatabaseCommandContext` already carries `DocumentId` (the local id/name) and an open-ended `Args` dictionary, so versioning specifics (`branchId`, `tag`, `sourceBranchId`, `dryRun`) ride in `Args` without any new context type:

```csharp
new DatabaseCommandContext(
    DatabaseCommandType.DeleteBranch,
    DocumentId: localName,
    Args: new Dictionary<string,string> { ["branchId"] = branchId });
```

> **Naming note.** `DatabaseCommandType` / `IDatabaseCommandAuthorizer` now governs more than "database" commands. We *keep* the names to avoid a breaking rename across hosts; the XML docs are updated to say "management & versioning commands". (A future major version could introduce `ManagementCommandType` aliases — out of scope here.)

### 4.2 Guard every `VersioningRouter` method

Inject the authorizer the same way `DatabaseCommandRouter` does (resolve per-call from a scope, so request-scoped identity flows correctly):

```csharp
private async ValueTask<bool> AuthorizeAsync(DatabaseCommandContext ctx, CancellationToken ct)
{
    using var scope = scopeFactory.CreateScope();
    var authorizer = scope.ServiceProvider.GetRequiredService<IDatabaseCommandAuthorizer>();
    return await authorizer.AuthorizeAsync(ctx, ct);
}
```

Every method gains a first-line guard returning `Forbidden<T>()` (mirroring the existing helper):

```csharp
public async Task<OpResult> DeleteBranchAsync(string localName, string branchId, CancellationToken ct = default)
{
    if (!await AuthorizeAsync(new DatabaseCommandContext(
            DatabaseCommandType.DeleteBranch, localName,
            new Dictionary<string,string> { ["branchId"] = branchId }), ct))
        return OpResult.Fail("Forbidden: Insufficient permissions for this operation.");
    // … existing body …
}
```

### 4.3 Owner-routing for versioning mutations

`ForkBranch`, `DeleteBranch`, `CreateVersion`, `DeleteVersion`, and `MergeBranch` (commit) all touch a **physical document id**. To match the cluster-safety guarantee of `DatabaseCommandRouter`, route them through the owner node so the live session (if any) is evicted before/after storage changes:

- For each affected physical id, resolve the branch → `branch.PhysicalDocumentId`.
- Acquire owner via `IDocumentOwnershipManager.GetOrAcquireOwnerAsync(physId, …)`.
- Execute locally if we are the owner, otherwise `backplane.SendRequestAsync(ownerNodeId, …)`.
- On delete, publish a `DocumentDeleted` (or new `BranchDeleted`) broadcast so caches drop cluster-wide.

This makes `VersioningRouter` implement `IBackplaneRequestExtension` exactly like `DatabaseCommandRouter`, with new backplane command constants (`ForkBranch`, `DeleteBranch`, `CreateVersion`, `DeleteVersion`, `MergeBranch`).

> **Effort note.** Read methods (`ListNames`, `ListBranches`, `ListVersions`, `ReadVersionSnapshot`) stay node-local — only authorization is added. Owner-routing is required *only* for the mutating set. The minimum viable security fix is §4.1 + §4.2 (authorization); §4.3 (owner-routing) is the correctness hardening and can land as a fast follow.

### 4.4 Policy examples (host side)

The host implements one authorizer and switches on the command. Example role model — *readers* can read, *editors* can fork/tag/merge, *owners* can delete, *admins* can purge tenant:

```csharp
public ValueTask<bool> AuthorizeAsync(DatabaseCommandContext ctx, CancellationToken ct)
{
    var role = _currentUser.RoleFor(ctx.DocumentId);   // host's own lookup
    bool ok = ctx.Command switch
    {
        // reads
        ListDocuments or ListNames or ListBranches or ListVersions
            or GetDocumentInfo or GetSnapshot or ListMilestones
            or ReadVersionSnapshot              => role >= Role.Reader,

        // mutating but non-destructive
        RegisterName or ForkBranch or CreateVersion
            or CompactDocument or MergeBranch   => role >= Role.Editor,

        // destructive (per-document)
        DeleteDocument or DeleteBranch or DeleteVersion
            or DeleteName or PurgeHistory       => role >= Role.Owner,

        // tenant-wide
        PurgeTenant                             => role >= Role.Admin,

        _ => false,
    };
    return ValueTask.FromResult(ok);
}
```

Because dry-run vs commit merge share `MergeBranch`, a host that wants to allow previews to everyone but commits to editors checks `ctx.Args?["dryRun"]`.

---

## 5. Complete Read + Delete Taxonomy

The principle: **every stored artifact has a read path and a delete path, with documented cascade behavior.**

| Artifact | Read | Delete | Cascade / safety |
|---|---|---|---|
| Document (hot) | `GetDocumentInfo`, `GetSnapshot`, `ListDocuments` | `DeleteDocument` | Evicts live session; deletes hot store; best-effort history wipe; `DocumentDeleted` broadcast. *Existing.* |
| Op history (cold) | `ListMilestones`, *(read snapshot at revision — see §5.4)* | `PurgeHistory(upToRevision)` | Respects tag pins (§5.2). *Existing, pin-aware after §5.2.* |
| Tenant | `ListDocuments` / `ListNames` (scoped) | `PurgeTenant` | Fan-out evict + bulk delete across hot & cold. *Existing.* |
| Name | `ListNames` | **`DeleteName` (new)** | Refuses unless all branches deleted (or cascades — configurable, §5.3). |
| Branch | `ListBranches`, `ReadVersionSnapshot` | `DeleteBranch` | Refuses if it has child forks (already enforced); now also authorized + owner-routed. |
| Version (tag) | `ListVersions`, `ReadVersionSnapshot` | **`DeleteVersion` (new)** | Removes `VersionRef` + unpins history; optionally drops the named history snapshot (§5.2). |

### 5.1 What already exists (just needs auth + SDK)

Reads: `ListDocuments`, `GetDocumentInfo`, `GetSnapshot`, `ListMilestones`, `ListNames`, `ListBranches`, `ListVersions`, `ReadVersionSnapshot`.
Deletes/destructive: `DeleteDocument`, `CompactDocument`, `PurgeHistory`, `PurgeTenant`, `DeleteBranch`.

### 5.2 New primitive — `DeleteVersion`

Tags are created by `CreateVersionAsync`, which (a) writes a **named history snapshot** (`tag/{tag}`) so the bytes survive compaction, and (b) creates a `VersionRef`. Tags also **pin the compaction floor** via `GetMinPinnedRevisionAsync`. Without deletion, a tag is a permanent retention lock.

```csharp
public async Task<OpResult> DeleteVersionAsync(
    string localName, string branchId, string tag, CancellationToken ct = default)
```

Behavior:
1. Authorize (`DeleteVersion`).
2. Resolve `VersionRef`; if absent, return `Ok()` (idempotent).
3. Remove the `VersionRef` from `IDocumentRefStore` (this immediately unpins compaction).
4. **Named history snapshot:** by default *leave* it (other tags or history reads may reference that revision). Optionally hard-delete if a new `Args["dropSnapshot"]=="true"` is set and the backend supports milestone deletion. This requires a small `IHistoryStore` addition (`DeleteMilestoneAsync` / `DeleteHistorySnapshotAsync`) guarded by `NotSupportedException` like the other optional cold-store ops.

> **Requires `IDocumentRefStore.DeleteVersionAsync`** — check whether the ref store already exposes this. If not, it's a one-method addition mirroring `DeleteBranchAsync`.

### 5.3 New primitive — `DeleteName` (optional but recommended)

A name without a delete path leaks ref-store rows forever once a document is retired.

```csharp
public async Task<OpResult> DeleteNameAsync(
    string localName, bool cascade = false, CancellationToken ct = default)
```

- `cascade == false`: refuse if any branch still exists ("delete branches first") — the safe default.
- `cascade == true`: delete every branch (each via the owner-routed `DeleteBranch` path so sessions evict + storage clears), then all versions, then the name row. Authorized as `DeleteName` (host can require `Admin`/`Owner`).

### 5.4 Read parity gap — historical snapshot read

`DatabaseCommandRouter` can read the *current* snapshot (`GetSnapshot`) and list milestones, but there is no management command to read **the snapshot at an arbitrary historical revision** (the capability `HistoryManager.ReconstructStateAtRevisionAsync` already provides internally). For full read coverage add:

```csharp
public Task<OpResult<DocumentSnapshot?>> GetHistorySnapshotAsync(
    string localDocumentId, long atRevision, CancellationToken ct = default)
// DatabaseCommandType.GetSnapshot is reused, with Args["atRevision"].
```

This is read-only and node-local. (Optional; include if "time-travel read" is a user requirement.)

---

## 6. Client SDK — `IOpStreamManagementClient`

A typed, transport-agnostic interface parallel to `IOpStreamClient`, living in `OpStream.Client.Transports`.

```csharp
namespace OpStream.Client.Transports;

/// <summary>
/// Typed management & access client: read and delete documents, history,
/// names, branches and versions. Mirrors IOpStreamClient's ergonomics but
/// targets the control plane rather than the collaboration path.
/// All ids/names are LOCAL ids; tenant scoping is implicit on the server.
/// </summary>
public interface IOpStreamManagementClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);

    // ── Documents / history (DatabaseCommandRouter) ────────────────
    Task<IReadOnlyList<DocumentInfo>>   ListDocumentsAsync(DocumentQuery query, CancellationToken ct = default);
    Task<DocumentInfo?>                 GetDocumentInfoAsync(string documentId, CancellationToken ct = default);
    Task<DocumentSnapshot?>             GetSnapshotAsync(string documentId, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryMilestone>> ListMilestonesAsync(string documentId, CancellationToken ct = default);
    Task DeleteDocumentAsync(string documentId, CancellationToken ct = default);
    Task CompactDocumentAsync(string documentId, long upToRevision, CancellationToken ct = default);
    Task PurgeHistoryAsync(string documentId, long upToRevision, CancellationToken ct = default);
    Task<int> PurgeTenantAsync(CancellationToken ct = default);

    // ── Names / branches / versions (VersioningRouter) ─────────────
    Task<IReadOnlyList<DocumentNameInfo>> ListNamesAsync(CancellationToken ct = default);
    Task DeleteNameAsync(string name, bool cascade = false, CancellationToken ct = default);

    Task<IReadOnlyList<BranchRef>> ListBranchesAsync(string name, CancellationToken ct = default);
    Task<BranchRef> ForkBranchAsync(string name, string fromBranchId, string newBranchId, long? atRevision = null, CancellationToken ct = default);
    Task DeleteBranchAsync(string name, string branchId, CancellationToken ct = default);

    Task<IReadOnlyList<VersionRef>> ListVersionsAsync(string name, string branchId, CancellationToken ct = default);
    Task<VersionRef> CreateVersionAsync(string name, string branchId, string tag, CancellationToken ct = default);
    Task<DocumentSnapshot?> ReadVersionSnapshotAsync(string name, string branchId, string tag, CancellationToken ct = default);
    Task DeleteVersionAsync(string name, string branchId, string tag, CancellationToken ct = default);

    Task<MergeReport> MergeBranchAsync(string name, string targetBranchId, string sourceBranchId, bool dryRun = false, CancellationToken ct = default);
}
```

Design notes:

- **Separate from `IOpStreamClient`.** Management is a different lifecycle, a different permission tier, and a different hub/endpoint. A given app may hold one, the other, or both. Keeping them separate avoids bloating the per-document collab client.
- **Same DTOs.** `DocumentInfo`, `DocumentSnapshot`, `HistoryMilestone`, `DocumentNameInfo`, `BranchRef`, `VersionRef`, `MergeReport`, `DocumentQuery` are reused as-is. If any currently live only in `OpStream.Server.Models`, the serializable shapes move to a shared/abstractions assembly the client already references.
- **Error mapping.** Servers throw `HubException` (SignalR) / error frames (WS) on `OpResult` failure. The client translates these into a typed `OpStreamManagementException` (carrying the server message, including the `"Forbidden: …"` string) so callers can distinguish authz failures from not-found.

### 6.1 SignalR implementation

`SignalROpStreamManagementClient` opens connections to the two existing hubs (management + versioning), invoking by the `OpStreamConstants.*HubMethods.*` names already defined. This is a thin marshalling layer; no server change needed for the database plane.

### 6.2 WebSocket implementation

`WebSocketOpStreamManagementClient` speaks the `WebSocketManagementMessage` framing for the database plane (already exists) and the **new** versioning framing from §7.

### 6.3 gRPC implementation

`gRPCOpStreamManagementClient` calls the generated `OpStreamManagementService` stub for the database plane (already exists in the proto) and the **new** `OpStreamVersioningService` stub from §7. gRPC already maps `Forbidden:` → `StatusCode.PermissionDenied` in `gRPCManagementTransport`; the new versioning service follows the same convention, so the client surfaces authz failures uniformly as `OpStreamManagementException` regardless of transport.

---

## 7. Transport Parity — WebSocket & gRPC Versioning

The database plane lives on all three transports; the versioning plane must catch up on the two that are missing.

### 7.1 WebSocket

Add `WebSocketVersioningMessage` + `WebSocketVersioningTransport` mirroring the existing `WebSocketManagementMessage` / `WebSocketManagementTransport`:

- A discriminated message envelope (op = `ListNames` | `ListBranches` | `ForkBranch` | `DeleteBranch` | `CreateVersion` | `ListVersions` | `ReadVersionSnapshot` | `DeleteVersion` | `MergeBranch` | `DeleteName`) with a JSON payload and a correlation id.
- Each op delegates to the (now authorized) `VersioningRouter`, returning the `OpResult` value or an error frame.

### 7.2 gRPC

Add an `OpStreamVersioningService` to `src/Protos/opstream.proto`, alongside the existing `OpStreamService` / `OpStreamManagementService` / `OpStreamCommentsService`, plus a `gRPCVersioningTransport` mirroring `gRPCManagementTransport`:

```protobuf
service OpStreamVersioningService {
  rpc ListNames           (VerEmptyRequest)        returns (VerListNamesResponse);
  rpc DeleteName          (VerDeleteNameRequest)   returns (VerOkResponse);
  rpc ListBranches        (VerNameRequest)         returns (VerListBranchesResponse);
  rpc ForkBranch          (VerForkBranchRequest)   returns (VerBranchResponse);
  rpc DeleteBranch        (VerBranchRequest)       returns (VerOkResponse);
  rpc ListVersions        (VerBranchRequest)       returns (VerListVersionsResponse);
  rpc CreateVersion       (VerCreateVersionRequest) returns (VerVersionResponse);
  rpc ReadVersionSnapshot (VerVersionRequest)      returns (VerGetSnapshotResponse);
  rpc DeleteVersion       (VerVersionRequest)      returns (VerOkResponse);
  rpc MergeBranch         (VerMergeRequest)        returns (VerMergeReportResponse);
}
```

The transport delegates each rpc to the (now authorized) `VersioningRouter` and reuses the same `Forbidden:` → `StatusCode.PermissionDenied` mapping `gRPCManagementTransport` already uses.

> **Build note.** The repo has a known Grpc.Tools Windows path bug with a documented manual `protoc` workaround; regenerating from the extended `.proto` follows that same procedure.

After 7.1 + 7.2, the §2.1 matrix is fully symmetric: **both planes, all three transports, one authorizer, one client.**

---

## 8. Multitenancy & Clustering

No new model here — we **inherit** the existing guarantees by routing everything through the routers:

- **Tenant isolation:** every name/id is globalized via `IDocumentIdGlobalizer.ToGlobalId` before touching a store and localized via `ToLocalId` before returning. `ListDocuments` / `ListNames` enumerate by `GetCurrentTenantPrefix()`. The client never sees another tenant's prefix.
- **Owner routing (after §4.3):** destructive versioning ops resolve the owner node for the affected physical id and execute there, evicting the live session first — closing the "deleted on node A, still served from node B" hole.
- **Cache coherence:** deletes publish a cluster broadcast (`DocumentDeleted` / new `BranchDeleted`) so every node drops in-memory state. `PurgeTenant` already fans out `EvictTenant`.

---

## 9. Delete Safety Semantics (explicit contract)

Destructive operations are the riskiest part of this surface, so the semantics are spelled out:

| Operation | Idempotent? | Refusal conditions | Irreversible? |
|---|---|---|---|
| `DeleteDocument` | Yes (no-op if absent) | — | Yes (physical) |
| `PurgeHistory(upTo)` | Yes | Must not purge past the min tag-pinned revision (`GetMinPinnedRevisionAsync`) | Yes |
| `DeleteBranch` | Yes | Refused if it has child forks (`ForkParentBranchId` match) | Yes |
| `DeleteVersion` | Yes | — | Tag ref always; snapshot bytes only if `dropSnapshot` |
| `DeleteName` | Yes | Refused if branches exist and `cascade==false` | Yes |
| `PurgeTenant` | Yes | Admin-only by convention | Yes — entire tenant |

Cross-cutting rules:

- **Authorize before any side effect.** The guard is the first statement in every method.
- **Pin guard before purge.** `PurgeHistory` and compaction consult `GetMinPinnedRevisionAsync` so a tagged version can never be silently destroyed. Deleting the tag (`DeleteVersion`) is the explicit way to release that floor.
- **Eviction before storage mutation** (owner-routed), never after.
- Deletes are **physical and immediate** in this proposal. Soft-delete is §10.

---

## 10. Optional Extensions (explicitly out of scope, noted for completeness)

1. **Soft delete / trash bin.** A `DeletedAt` tombstone on refs + a `RestoreAsync`, with a background reaper honoring a retention window. Useful for accidental-deletion recovery; adds state-machine complexity. Can layer on the same authorizer (`RestoreDocument`, `EmptyTrash`).
2. **Bulk / query-scoped delete.** `DeleteDocumentsAsync(DocumentQuery)` for admin cleanup. Higher blast radius — gate behind a distinct command type.
3. **Audit log.** Emit a structured event per management command (who/what/when/result) through an `IManagementAuditSink`. Natural to add at the router seam since every op already funnels through `AuthorizeAsync`.
4. **Export / backup before delete.** `ExportDocumentAsync` returning a portable bundle (snapshot + op log + refs), so a delete can be preceded by a download.

---

## 11. Incremental Rollout Plan

Ordered by value/risk; each step ships independently.

1. **Security fix (highest priority).** §4.1 + §4.2 — extend `DatabaseCommandType`, add `AuthorizeAsync` guards to every `VersioningRouter` method. Closes the unauthenticated-delete hole with no transport/client changes. *Small, isolated, high impact.*
2. **Missing delete primitives.** §5.2 `DeleteVersion`, §5.3 `DeleteName` (+ any required `IDocumentRefStore` / `IHistoryStore` method, each `NotSupportedException`-guarded).
3. **Cluster hardening.** §4.3 owner-routing + broadcast eviction for versioning mutations (`VersioningRouter : IBackplaneRequestExtension`).
4. **Client SDK over SignalR.** §6 + §6.1 `IOpStreamManagementClient` + `SignalROpStreamManagementClient`. Unblocks real consumers immediately (SignalR already exposes both planes).
5. **Transport parity.** §7.1 WebSocket + §7.2 gRPC versioning transports, then the matching §6.2 / §6.3 WS and gRPC management clients.
6. **Read parity nicety.** §5.4 historical-snapshot read (`GetHistorySnapshotAsync`), if time-travel read is wanted.

Steps 1–2 are the critical path; 3–6 are progressive enhancement.

---

## 12. Testing Strategy

- **Authorization matrix.** For every `DatabaseCommandType`, a test asserting allow/deny is honored (authorizer returning `false` ⇒ `Forbidden` / `HubException`, *no* side effect). Especially the formerly-unguarded versioning ops.
- **Idempotency.** Double-delete of document / branch / version / name returns success without throwing.
- **Refusal conditions.** Delete branch with child fork ⇒ fail; `DeleteName(cascade:false)` with branches ⇒ fail; `PurgeHistory` past a tag pin ⇒ floored.
- **Cluster behavior.** With a multi-node TestContainers/Redis-backplane setup (the repo already has Redis backplane + TestContainers infra), assert that a delete on a non-owner node evicts the live session on the owner and broadcasts eviction.
- **Tenant isolation.** A client in tenant A cannot list/read/delete tenant B's documents, branches, or versions; returned ids are always local.
- **Round-trip SDK tests.** `IOpStreamManagementClient` against all three transports (SignalR, WebSocket, gRPC): create name → fork → tag → read version → merge (dry-run + commit) → delete version → delete branch → delete name, asserting state at each step. The same authz/idempotency assertions must pass identically on every transport.
- **Compaction pin integration.** Create version → compact/purge → bytes survive; delete version → compact → floor releases.

---

## 13. Summary

OpStream already has the storage, history, and versioning primitives. What's missing is a **safe, uniform way to read and delete them**. This proposal:

- Puts **one authorization seam** (`IDatabaseCommandAuthorizer` + an extended `DatabaseCommandType`) in front of *both* control planes, immediately closing the unauthenticated-versioning hole.
- Fills the **delete coverage gaps** (`DeleteVersion`, `DeleteName`) and defines explicit cascade/pin/eviction semantics.
- Brings versioning up to the **cluster-safety and transport parity** the database plane already has.
- Ships a **typed `IOpStreamManagementClient`** so consumers get the same ergonomics they enjoy on the collaboration path.

The work is additive and sequenced so the **security fix (step 1) can land first and alone**, with the SDK and transport parity following as progressive enhancements.
