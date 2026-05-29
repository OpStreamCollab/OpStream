# Technical Design Proposal — Decomposing `DocumentRouter`

- **Status:** Draft / for discussion
- **Author:** (proposal)
- **Date:** 2026-05-29
- **Scope:** Server-side session orchestration. Splits the `DocumentRouter` god-class
  (`src/OpStream.Server/Session/DocumentRouter.cs`, **759 lines**) into a thin facade plus a
  set of single-responsibility collaborators, **without changing the public API** that
  transports and the management/comment routers depend on.

---

## 1. Motivation

`DocumentRouter` started as "the entry point on the server side" and has accreted nearly
every server-side concern that touches a live document. It is now 759 lines, holds **six**
concurrent dictionaries, and mixes request handling, clustering, persistence, presence,
timers, diagnostics, and host callbacks in one type.

The symptom is the line count; the disease is **too many reasons to change**. A change to
the idle-timeout policy, the cluster-proxy protocol, the drain/delete behavior, or the
diagnostics shape all force edits to the same file — and there is **no unit test** that
constructs a `DocumentRouter` directly (the only coverage is at the `DocumentSession` level
and through integration tests), precisely because its dependency surface is too large to set
up in isolation.

This document proposes an **incremental, behavior-preserving** decomposition.

### Goals

1. **One responsibility per type.** Each extracted collaborator has a single reason to change.
2. **No breaking changes** to the public surface used by transports,
   `DatabaseCommandRouter`, and `CommentRouter`. `DocumentRouter` remains the injected
   facade; only its internals move.
3. **Unit-testable units.** Each collaborator is independently constructable and mockable.
4. **Ship in small steps.** Every extraction is independently mergeable with green tests.

### Non-goals

- Changing the wire protocol, the backplane contract, or storage interfaces.
- Reworking the collaboration algorithm (OT/CRDT transform, rebase) — that lives in
  `DocumentSession`/`IOpEngine` and is out of scope.
- Changing multi-tenancy, authorization semantics, or the drain/delete feature behavior.

---

## 2. Current state — responsibility audit

A single class currently owns all of the following. Each row is an independent "reason to
change", i.e. a distinct responsibility.

| # | Responsibility | State it owns | Methods | Collaborators |
|---|---|---|---|---|
| 1 | **Public collaboration API** (facade) | — | `JoinDocumentAsync`, `ApplyOpAsync`, `UpdateAwarenessAsync` | everything below |
| 2 | **Cluster routing + auth pipeline** | — | `ExecuteRoutedAsync` (authorize → resolve owner → run-local-or-proxy) | `IDocumentAuthorizer`, `IDocumentOwnershipManager`, `IBackplane` |
| 3 | **Inbound backplane dispatch** | `RequestExtensions`, `OnBackplaneMessage` | `HandleIncomingRequestAsync`, the `*RequestData` records | `IBackplane`, `IBackplaneRequestExtension` |
| 4 | **Backplane subscriptions** | `_backplaneSubscriptions` | `EnsureBackplaneSubscriptionAsync` | `IBackplane` |
| 5 | **Document-session registry & lifecycle** | `_activeSessions`, `_sessionLocks` | `GetSessionAsync`, `OpenSessionAsync`, `CloseSessionAsync`, `TryGetActiveSession`, `GetActiveDocumentIds`, `EvictSessionAsync` | `IDocumentSessionFactory`, `IDocumentStore` |
| 6 | **Idle-timeout management** | `_idleTimers`, `IdleTimeout` | `ScheduleSessionClosure` | `ITimerFactory` |
| 7 | **Awareness-session registry** | `_activeAwareness` | `GetAwarenessSessionAsync` | `IBackplane` |
| 8 | **Peer presence tracking** | `_peerDocuments` | `GetDocumentsId`, `RemovePeerFromAllSessionsAsync` | sessions, awareness, backplane |
| 9 | **Document drain + delete** | — | `NotifyDrainHandlersAsync`, `DeleteDrainedDocumentAsync` | `IDocumentDrainHandler`, stores, ownership |
| 10 | **Diagnostics** | — | `GetDiagnosticsSnapshotAsync` | `IDocumentStore`, ownership |
| 11 | **Startup validation** | — | `InitializeAsync` (storage/backplane/engine warnings) | stores, backplane |

Eleven responsibilities in one class. Several also share the `_sessionLocks` striping in
subtle ways (see §6).

### 2.1 Smells this produces

- **Wide constructor / DI surface** — six injected services + a lazily-resolved
  `IServiceProvider` to break a circular dependency (`DocumentRouter → IBackplaneRequestExtension → DatabaseCommandRouter/CommentRouter → DocumentRouter`).
- **Hidden coupling between dictionaries** — `CloseSessionAsync` mutates five of the six
  dictionaries at once; getting that ordering wrong is a leak or a race.
- **Untestable in isolation** — no `DocumentRouterTests` exists; behavior is only exercised
  end-to-end.
- **Change amplification** — the recent "drain/delete" feature had to be threaded straight
  into the middle of `RemovePeerFromAllSessionsAsync`, growing the class further.

---

## 3. Design overview

Keep `DocumentRouter` as a **thin facade** implementing the same public methods, delegating
to focused collaborators. Transports and the other routers keep calling
`router.JoinDocumentAsync(...)` etc. unchanged.

```
                         ┌───────────────────────────────────────────┐
   transports  ───────►  │            DocumentRouter (facade)         │
   (SignalR/WS/gRPC)     │   Join / ApplyOp / UpdateAwareness /       │
   DatabaseCommandRouter │   RemovePeer / Evict / Diagnostics         │
   CommentRouter         └───┬───────┬───────┬───────┬───────┬────────┘
                             │       │       │       │       │
        ┌────────────────────┘       │       │       │       └─────────────────────┐
        ▼                            ▼       ▼       ▼                              ▼
 ┌────────────────┐   ┌──────────────────┐ ┌──────────────┐ ┌────────────────┐ ┌───────────────┐
 │ Execution      │   │ DocumentSession  │ │ Awareness    │ │ PeerRegistry   │ │ DrainCoord-   │
 │ Pipeline       │   │ Registry         │ │ SessionReg.  │ │                │ │ inator        │
 │ (auth+owner+   │   │ (open/close/     │ │              │ │ peer→docs map  │ │ notify+delete │
 │  proxy)        │   │  idle timers)    │ │              │ │                │ │               │
 └───────┬────────┘   └────────┬─────────┘ └──────────────┘ └────────────────┘ └───────┬───────┘
         │                     │                                                        │
         ▼                     ▼                                                        ▼
 ┌────────────────┐   ┌──────────────────┐                                  ┌────────────────────┐
 │ Backplane      │   │ KeyedAsyncLock   │                                  │ IDocumentDrainHand. │
 │ Gateway        │   │ (per-doc locks)  │                                  │ (host)              │
 │ (dispatch+subs)│   └──────────────────┘                                  └────────────────────┘
 └────────────────┘
```

---

## 4. Proposed components

Each is an interface + implementation, registered in DI alongside the existing services.

### 4.1 `IDocumentSessionRegistry` — session lifecycle (responsibilities 5 + 6)

Owns `_activeSessions`, opening/closing, and idle-timeout scheduling.

```csharp
public interface IDocumentSessionRegistry
{
    /// Returns the live session if present on this node, else null (no open).
    IDocumentSession? TryGet(string documentId);

    /// Opens (loading snapshot + replaying ops) or returns the existing session.
    Task<IDocumentSession> GetOrOpenAsync(string documentId, string documentType, CancellationToken ct = default);

    /// Closes and disposes the session and its idle timer. Safe if absent.
    Task CloseAsync(string documentId);

    IReadOnlyList<string> ActiveDocumentIds { get; }

    /// (Re)arms the idle timer that calls CloseAsync after the configured timeout.
    void ScheduleIdleClosure(string documentId);
    void CancelIdleClosure(string documentId);
}
```

- Absorbs `GetSessionAsync`, `OpenSessionAsync`, `CloseSessionAsync`,
  `ScheduleSessionClosure`, `TryGetActiveSession`, `GetActiveDocumentIds`, `_idleTimers`,
  `IdleTimeout`.
- Idle timeout becomes a real option (`SessionRegistryOptions.IdleTimeout`) instead of a
  hard-coded `static readonly` — a small bonus that resolves an existing TODO.
- Depends on `IDocumentSessionFactory`, `IDocumentStore`, `ITimerFactory`,
  `IDocumentLockRegistry` (§6).

### 4.2 `IAwarenessSessionRegistry` — presence sessions (responsibility 7)

Owns `_activeAwareness` and `GetAwarenessSessionAsync`.

```csharp
public interface IAwarenessSessionRegistry
{
    Task<IAwarenessSession> GetOrCreateAsync(string documentId, CancellationToken ct = default);
    Task CloseAsync(string documentId);
}
```

### 4.3 `IPeerRegistry` — peer → document membership (responsibility 8, data only)

Owns `_peerDocuments`. Pure bookkeeping; no I/O.

```csharp
public interface IPeerRegistry
{
    void Track(string peerId, string documentId);
    string[] DocumentsFor(string peerId);
    /// Removes the peer and returns the documents it was in.
    IReadOnlyCollection<string> Remove(string peerId);
}
```

The *orchestration* of "peer left → leave each session → maybe drain → notify backplane"
stays in the facade (it spans multiple collaborators), but the map itself moves here.

### 4.4 `IDocumentExecutionPipeline` — auth + ownership + proxy (responsibility 2)

The generic `ExecuteRoutedAsync<TResult,TRequestData>` is the most reused and most subtle
piece. Extract it verbatim.

```csharp
public interface IDocumentExecutionPipeline
{
    Task<OpResult<TResult>> ExecuteAsync<TResult, TRequestData>(
        string documentId,
        bool isProxied,
        Func<DocumentAccess, bool> permissionCheck,
        string backplaneCommand,
        TRequestData proxyData,
        Func<CancellationToken, Task<IDocumentSession>> sessionProvider,
        Func<IDocumentSession, CancellationToken, Task<TResult>> localAction,
        CancellationToken ct);
}
```

- Depends on `IDocumentAuthorizer` (resolved per-call in a scope, as today),
  `IDocumentOwnershipManager`, `IBackplane`.
- This is where the "authorize, then resolve owner, then run locally or proxy" rule lives —
  the single place that decides *where* an operation runs.

### 4.5 `IDocumentBackplaneGateway` — inbound dispatch + subscriptions (responsibilities 3 + 4)

Owns `HandleIncomingRequestAsync`, the `*RequestData` records, `EnsureBackplaneSubscriptionAsync`,
`_backplaneSubscriptions`, and registering the request handler in `InitializeAsync`.

```csharp
public interface IDocumentBackplaneGateway
{
    Task StartAsync(CancellationToken ct = default);                 // RegisterRequestHandlerAsync
    Task EnsureSubscribedAsync(string documentId, CancellationToken ct = default);
    event Func<string, BackplaneMessage, Task>? OnDocumentMessage;   // = today's OnBackplaneMessage
}
```

It calls back into the facade's `JoinDocumentAsync/ApplyOpAsync/UpdateAwarenessAsync` with
`isProxied: true` for the "I am the owner, a peer node is asking me to do this" path. The
`IBackplaneRequestExtension` fan-out (for `DatabaseCommandRouter`/`CommentRouter`) moves here
too, keeping the lazy-resolution trick that breaks the circular dependency.

### 4.6 `IDocumentDrainCoordinator` — drain + delete (responsibility 9)

The feature just added. Extract `NotifyDrainHandlersAsync` + `DeleteDrainedDocumentAsync`.

```csharp
public interface IDocumentDrainCoordinator
{
    /// Invokes IDocumentDrainHandler(s); returns the aggregate decision.
    Task<DocumentDrainDecision> NotifyAsync(IDocumentSession session, CancellationToken ct = default);

    /// Closes the session and deletes all of the document's data + broadcasts eviction.
    Task DeleteAsync(string documentId, CancellationToken ct = default);
}
```

- Depends on `IServiceScopeFactory` (to resolve scoped `IDocumentDrainHandler`s),
  `IDocumentSessionRegistry`, `IDocumentStore`, `IHistoryStore`, `IBackplane`,
  `IDocumentOwnershipManager`.
- Isolating this makes the delete-cascade independently testable and keeps host-callback
  policy out of the lifecycle registry.

### 4.7 `IDocumentDiagnosticsService` — read-only inspection (responsibility 10)

Extract `GetDiagnosticsSnapshotAsync`. Pure read model over the registry + store; no mutable
state. Already cleanly separable.

### 4.8 `OpStreamStartupValidator` — startup warnings (responsibility 11)

The storage/backplane/engine warning block in `InitializeAsync`. Naturally a hosted service
or a one-shot validator invoked at startup; it has no business sitting on the request-path
class.

### 4.9 `DocumentRouter` — the remaining facade

After extraction the facade keeps only:

- The public methods (unchanged signatures): `JoinDocumentAsync`, `ApplyOpAsync`,
  `UpdateAwarenessAsync`, `RemovePeerFromAllSessionsAsync`, `EvictSessionAsync`,
  `CloseSessionAsync`, `GetDocumentsId`, `GetActiveDocumentIds`, `TryGetActiveSession`,
  `GetDiagnosticsSnapshotAsync`, `InitializeAsync`, the `OnBackplaneMessage` event, and the
  `Backplane` property.
- Thin orchestration bodies that delegate. For example:

```csharp
public Task<OpResult<OpApplyResult>> ApplyOpAsync(
    string peerId, string documentId, ReadOnlyMemory<byte> payload, long baseRevision,
    bool isProxied = false, CancellationToken ct = default)
    => _pipeline.ExecuteAsync<OpApplyResult, ApplyOpRequestData>(
        documentId, isProxied,
        access => access.CanWrite,
        OpStreamConstants.BackplaneCommands.ApplyOp,
        new ApplyOpRequestData(documentId, peerId, payload.ToArray(), baseRevision),
        async ct => await _sessions.GetOrOpenAsync(documentId, /*type*/ ..., ct) /* see note */,
        (session, ct) => session.ApplyOpAsync(peerId, payload, baseRevision, ct),
        ct);
```

`RemovePeerFromAllSessionsAsync` remains the one genuinely cross-cutting orchestration and
keeps living on the facade, but now reads as a clear sequence of delegations:

```csharp
public async Task RemovePeerFromAllSessionsAsync(string peerId)
{
    foreach (var documentId in _peers.Remove(peerId))
    {
        var session = _sessions.TryGet(documentId);
        if (session is not null)
        {
            await session.LeaveAsync(peerId);
            if (session.ActivePeersCount == 0)
            {
                var decision = await _drain.NotifyAsync(session);
                if (decision == DocumentDrainDecision.Delete) await _drain.DeleteAsync(documentId);
                else                                          _sessions.ScheduleIdleClosure(documentId);
            }
        }
        await _awareness.LeaveAsync(documentId, peerId);
        await _backplane.PublishAsync(documentId, PeerDisconnectedMessage(peerId));
    }
}
```

Target: facade drops from **759** lines to roughly **120–160**.

---

## 5. Old → new mapping

| Today (in `DocumentRouter`) | Moves to |
|---|---|
| `_activeSessions`, `GetSessionAsync`, `OpenSessionAsync`, `CloseSessionAsync`, `TryGetActiveSession`, `GetActiveDocumentIds` | `IDocumentSessionRegistry` |
| `_idleTimers`, `IdleTimeout`, `ScheduleSessionClosure` | `IDocumentSessionRegistry` (+ `SessionRegistryOptions`) |
| `_activeAwareness`, `GetAwarenessSessionAsync` | `IAwarenessSessionRegistry` |
| `_peerDocuments`, `GetDocumentsId` | `IPeerRegistry` |
| `ExecuteRoutedAsync` | `IDocumentExecutionPipeline` |
| `HandleIncomingRequestAsync`, `*RequestData`, `EnsureBackplaneSubscriptionAsync`, `_backplaneSubscriptions`, `RequestExtensions` | `IDocumentBackplaneGateway` |
| `NotifyDrainHandlersAsync`, `DeleteDrainedDocumentAsync` | `IDocumentDrainCoordinator` |
| `GetDiagnosticsSnapshotAsync` | `IDocumentDiagnosticsService` |
| `InitializeAsync` warnings | `OpStreamStartupValidator` |
| `EvictSessionAsync`, `RemovePeerFromAllSessionsAsync`, public API | **stays** on `DocumentRouter` facade |

---

## 6. Cross-cutting concern — per-document locking

Today `_sessionLocks` is a striped `ConcurrentDictionary<string, SemaphoreSlim>` shared by
`GetSessionAsync`, `OpenSessionAsync`, and `GetAwarenessSessionAsync`, and **disposed** by
`CloseSessionAsync`. Splitting session and awareness registries naively would either
duplicate the lock map or risk one registry disposing a lock the other still holds.

**Proposal:** extract a tiny shared utility:

```csharp
public interface IDocumentLockRegistry
{
    Task<IDisposable> AcquireAsync(string documentId, CancellationToken ct = default);
}
```

- A single keyed-semaphore registry injected into both registries and the drain coordinator.
- Centralizes the "acquire per-doc lock, create-if-absent, release" pattern that is currently
  copy-pasted three times.
- Fixes a latent bug: lock **disposal** lifecycle is owned in exactly one place, so closing a
  session can no longer yank a semaphore out from under awareness creation.

This is the highest-value correctness win hiding inside the refactor.

---

## 7. Incremental rollout plan

Ordered low-risk → higher-risk; each step compiles, keeps the public API, and ships with
green tests.

1. **Extract `IDocumentDiagnosticsService`.** Pure read model, zero shared mutable state —
   the safest first cut and a template for the pattern.
2. **Extract `OpStreamStartupValidator`.** Move the warning block out of `InitializeAsync`.
3. **Extract `IPeerRegistry`.** Pure data move; mechanical.
4. **Extract `IDocumentDrainCoordinator`.** Self-contained; already has tests
   (`DocumentDrainTests`) that must stay green — a built-in safety net.
5. **Introduce `IDocumentLockRegistry`** and route the three lock sites through it (§6).
6. **Extract `IAwarenessSessionRegistry`** (now that locking is centralized).
7. **Extract `IDocumentSessionRegistry`** (the big one) including idle timers + options.
8. **Extract `IDocumentExecutionPipeline`** (`ExecuteRoutedAsync`).
9. **Extract `IDocumentBackplaneGateway`** (inbound dispatch + subscriptions).
10. **Collapse the facade** — `DocumentRouter` is now pure delegation.

Steps 1–4 deliver most of the readability win with minimal risk; 5–9 are the structural core.

---

## 8. Testing strategy

The refactor is justified largely *because* it unlocks testing:

- **Characterization tests first.** Before extracting, add a `DocumentRouterTests` harness
  using the real DI graph (as `DocumentDrainTests` already does) to pin current behavior:
  join → apply → leave → idle-close, proxy path, evict, drain-keep/drain-delete. These are
  the regression net for every extraction step.
- **Per-collaborator unit tests** after each extraction:
  - `DocumentSessionRegistry` — open/replay, idempotent get, idle close, evict.
  - `DocumentExecutionPipeline` — authorize-deny ⇒ `Forbidden`; owner-local vs proxy paths
    (mock `IDocumentOwnershipManager`/`IBackplane`).
  - `DrainCoordinator` — decision aggregation (any `Delete` wins), delete cascade, handler
    exception isolation. (`DocumentDrainTests` largely covers this already.)
  - `PeerRegistry` — track/remove/return-documents.
  - `DocumentLockRegistry` — mutual exclusion per key, independence across keys.
- **No behavior change** is the acceptance bar: the characterization suite must pass
  unmodified after every step.

---

## 9. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Subtle concurrency regression when splitting the shared lock map | Do §6 (`IDocumentLockRegistry`) as a dedicated step with its own tests *before* splitting the registries. |
| Circular DI (`DocumentRouter ↔ DatabaseCommandRouter/CommentRouter`) | Keep the lazy `IServiceProvider.GetServices<IBackplaneRequestExtension>()` resolution inside `IDocumentBackplaneGateway`; do not eagerly inject. |
| Hidden ordering coupling in `CloseSessionAsync` (touches 5 maps) | Each registry owns and disposes only its own state; the facade calls `sessions.CloseAsync` + `awareness.CloseAsync` in a defined order, covered by a characterization test. |
| Large diff / review fatigue | Land as 10 small PRs (§7), not one. |
| `documentType` not available on the proxy `ApplyOp` path when opening a session | Already an existing constraint (ApplyOp assumes the session exists on the owner); preserve current behavior — `GetOrOpen` is only forced on the Join path. |

---

## 10. Alternatives considered

1. **Leave it as-is.** Rejected: the class keeps growing (the drain feature already had to be
   injected mid-method) and remains untestable in isolation.
2. **Full rewrite into an actor/mailbox model** (one mailbox per document serializing all
   ops/awareness/lifecycle). Architecturally attractive and would dissolve most of the
   locking, but it is a large, risky change to the hot path. Out of scope here; the facade
   decomposition is a prerequisite that would make such a move feasible later.
3. **Split only by "extract a couple of helpers"** without interfaces. Rejected: doesn't
   restore testability (the helpers would still need the full DI surface) and doesn't
   establish clear ownership boundaries.

---

## 11. Summary

`DocumentRouter` violates SRP by owning ~11 distinct responsibilities across 759 lines and
six concurrent dictionaries, with no isolated test coverage. This proposal keeps it as a thin
**facade** with an unchanged public API and extracts focused, individually testable
collaborators: a **session registry** (lifecycle + idle timers), an **awareness registry**, a
**peer registry**, an **execution pipeline** (auth + ownership + proxy), a **backplane
gateway** (inbound dispatch + subscriptions), a **drain coordinator** (notify + delete), a
**diagnostics service**, and a **startup validator** — coordinated through a shared
**per-document lock registry** that also fixes a latent lock-disposal hazard.

The work is sequenced into ten independently shippable steps, each guarded by a
characterization test suite, so the decomposition lands incrementally with no behavior change
and no transport-facing churn.
