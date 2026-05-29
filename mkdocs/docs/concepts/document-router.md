# Document Router

The `DocumentRouter` is the **single entry point** on the server side. Every transport —
SignalR, WebSocket, gRPC — funnels its join / op / awareness calls through this one
component, which means authorization, ownership, and routing logic live in exactly one
place regardless of how the client connected.

You don't normally instantiate it: `services.AddOpStream()` registers it as a singleton
and the transports resolve it from DI.

## Responsibilities

| Responsibility | What it does |
|---|---|
| **Authorization** | Every join / op / awareness call is checked against your [`IDocumentAuthorizer`](../operations/authorization.md) before any state is touched. |
| **Ownership** | In a multi-node cluster, exactly one node owns each document at a time. The router transparently proxies a call to the owning node when the current node isn't the owner. See the [Backplane ownership model](../operations/backplane.md#ownership-model). |
| **Session management** | Lazily creates a [Session](session.md) when the first [peer](peer.md) joins and reuses it for subsequent peers on the same node. |
| **Awareness fan-out** | Routes ephemeral [Awareness](../engines/awareness.md) updates to the other peers on the document. |
| **Idle cleanup** | Closes sessions after an inactivity timeout to free memory. |

## Why a single router

Concentrating these cross-cutting concerns in one component is what lets OpStream run
**multiple transports on the same process and port simultaneously**. A React client on
SignalR and a Python bot on raw WebSockets hit the same router, get the same
authorization decision, and land in the same session — they collaborate transparently.

## Relationship to the other routers

OpStream has three sibling routers, each owning one plane:

- **`DocumentRouter`** — the collaboration hot path (this page).
- **[Comment Router](comment-router.md)** — comment mutations, anchored under the session lock.
- **Database Command Router** — the management/versioning control plane (list, delete, compact, purge, fork, tag, merge), guarded by [`IDatabaseCommandAuthorizer`](../reference/configuration.md#authorization).

## See also

- [Session](session.md) — what the router creates and routes into.
- [Backplane](../operations/backplane.md) — the fabric the router uses to reach other nodes.
- [Authorization](../operations/authorization.md) — the contract the router enforces.
