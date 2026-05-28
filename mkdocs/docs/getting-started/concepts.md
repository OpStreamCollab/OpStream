# Core concepts

A small vocabulary that the rest of the documentation assumes you've internalized.

## Transport

A **transport** is the wire layer that connects clients to the OpStream server.
OpStream ships three transports; any combination can run on the same process and
the same port simultaneously:

| Transport | Best for | Client requirement |
|---|---|---|
| **SignalR** | Browsers, .NET apps, mobile with a SignalR library | Official SignalR client (JS, .NET, Java, Swift, …) |
| **WebSocket** | Any stack — the most universal option | Native `WebSocket` in every browser and language |
| **gRPC** | Backend-to-backend, strongly-typed contracts | Generated gRPC stub (11+ languages) |

Clients pick the transport that fits their stack. A React app on SignalR and a
Python bot on WebSocket can collaborate on the same document through the same
server at the same time. The transport is invisible to the engine and the storage
layers — it only carries ops and awareness in and fan-out out.

Enable transports at startup:

```bash
OPSTREAM__TRANSPORTS="signalr,websockets,grpc"
```

See [Transports](../transports/index.md) for per-transport configuration.

## Storage

**Storage** is where OpStream persists the op log and snapshots — the source of
truth that lets sessions be rebuilt after a restart or a failover.

Storage sits behind a single `IDocumentStore` interface. Swap the provider and
nothing else changes:

| Provider | Good for |
|---|---|
| `memory` | Local dev and tests — lost on restart |
| `sqlite` | Single-tenant edge boxes, small teams |
| `postgres` | Recommended default for new production projects |
| `mysql` | Existing MySQL / MariaDB shops |
| `sqlserver` | Microsoft stacks, Azure SQL |
| `mongo` | Document-heavy workloads, flexible schemas |
| `redis` | Lowest write latency, ephemeral or RDB-backed |

All EF Core providers run migrations on first connect — no manual `dotnet ef`
step. Switch provider with one env var:

```bash
OPSTREAM__STORAGE__PROVIDER=postgres
OPSTREAM__STORAGE__CONNECTIONSTRING="Host=...;Database=opstream;..."
```

See [Storage](../storage/index.md) for connection-string examples and
tuning options per backend.

## Document

A **document** is whatever your application calls "the thing two users
co-edit": a markdown file, a Notion page, a settings dialog, a spreadsheet,
a CAD model. OpStream doesn't care about its semantics — only that you've
picked an [engine](../engines/index.md) that knows how to merge concurrent
edits on its shape.

Each document has:

- A **document id** — opaque string, scoped by tenant (see
  [Multi-tenancy](../operations/multitenancy.md)).
- A **document type** — a discriminator string (`"text"`, `"rich-text"`,
  `"tree"`, …) that tells the router which engine and which session
  factory to use.
- A **state** of type `TDoc` — what the engine produces after applying ops.
- A **revision** counter — monotonic per document, incremented once per
  accepted op.

## Op

An **op** is a single, atomic change to the document. The engine knows
how to:

- `Apply` an op to a state, producing a new state.
- `Transform` an incoming op against a concurrent op so its intent
  survives the rebase.
- `Invert` an op against the pre-state, producing the op that undoes it.
- `Compose` two ops into one (when supported).
- `IsNoOp` — recognize an op that has no effect.

Engines either follow **Operational Transformation** (Text, Rich Text)
or **CRDT** semantics (JSON, Tree, Table, Form). The choice is internal —
your code interacts with the same `IOpEngine<TDoc, TOp>` contract either
way. See [Engine contracts](../reference/interfaces.md).

## Revision

The current revision is the index of the **last accepted op**. Clients
include their `baseRevision` with every op they send; if the server's
revision is higher, the server **rebases** the op through OT / CRDT
transforms before applying it.

This is what lets two clients edit the same document without locks: each
client thinks it's editing revision N, and the server reconciles.

## Peer

A **peer** is a single connected client — typically one browser tab or one
desktop app instance. Multiple peers under the same user account are
treated as independent for concurrency purposes; they're identified by a
**peer id** the transport assigns at connect time.

## Session

A `DocumentSession<TDoc, TOp>` is the in-memory home of an open document
on the server: it owns the current state, holds the apply lock, talks to
the store, and broadcasts via the backplane. Sessions are created lazily
when the first peer joins and closed after an idle timeout (default 5
minutes).

## Router

The `DocumentRouter` is the single entry point on the server side.
It handles:

- **Authorization** — every join / op / awareness call goes through your
  `IDocumentAuthorizer`.
- **Ownership** — in a multi-node cluster, exactly one node owns each
  document at a time. The router transparently proxies calls to the
  owning node.
- **Awareness** — presence data for connected peers.
- **Idle cleanup** — closes sessions after inactivity.

You don't typically instantiate it; you call `services.AddOpStream()`
and resolve it from DI.

## Awareness

Presence / cursors / "user is typing" / live selections — **ephemeral**
state that's broadcast in real time but never persisted to the op log.
Each peer publishes their state via `UpdateAwarenessAsync`; the server
keeps it for ~30 seconds, fans it out to other peers, and drops it on
disconnect or expiry. See [Awareness](../engines/awareness.md).

## Backplane

The cluster-wide pub/sub fabric. In single-node mode this is the
in-process `LocalBackplane`; for production you swap in `UseRedisBackplane()`.
Its job is to ensure that an op applied on node A is broadcast to peers
connected to nodes B, C, …, and that ownership of a document is moved
between nodes when needed. See [Backplane](../operations/backplane.md).

## Snapshot

To avoid re-applying every op from genesis on every load, OpStream takes
**snapshots** — compact serialized states tagged with their revision.
At rehydration time the session loads the latest snapshot and replays
only the ops applied after it. Snapshots are policy-driven (default: every
100 ops or 5 minutes). See [Snapshots](../operations/snapshots.md).

## All concepts at a glance

| Concept | Where it lives | What it owns |
|---|---|---|
| **Transport** | Network edge | Wire protocol between clients and server |
| **Storage** | External infra (DB / Redis / …) | Op log, snapshots, durable state |
| **Document** | Logical unit | Id, type, state, revision counter |
| **Op** | In-flight / op log | A single atomic change to the document |
| **Revision** | Per document | Monotonic counter; used to rebase concurrent ops |
| **Peer** | Per connection | One connected client, one peer id |
| **Session** | One per open document, one node | In-memory state, apply lock, persistence |
| **Router** | One per server process | Auth, ownership, routing, awareness |
| **Awareness** | Ephemeral, in-memory | Cursors, presence, "user typing" — never persisted |
| **Backplane** | Shared infra (Redis) | Cluster fan-out and ownership coordination |
| **Snapshot** | Storage | Compact state checkpoint; shortens replay on reload |
| **Engine** | Pure code | Apply / Transform / Invert / Compose / IsNoOp |

## Next: [Transports →](../transports/index.md)
