# Core concepts

A small vocabulary that the rest of the documentation assumes you've internalized. Each
concept below links to a page where it's covered in depth — skim this page once, then
follow the links when you need detail.

## All concepts at a glance

| Concept | Where it lives | What it owns | Learn more |
|---|---|---|---|
| **Anchor** | Comment metadata | A comment's position in the document, rebased as edits land | [Anchors →](../concepts/comments.md#anchors) |
| **Authorization** | Request edge | Who may join, edit, or manage a document | [Authorization →](../operations/authorization.md) |
| **Awareness** | Ephemeral, in-memory | Cursors, presence, "user typing" — never persisted | [Awareness →](../engines/awareness.md) |
| **Backplane** | Shared infra (Redis) | Cluster fan-out and ownership coordination | [Backplane →](../operations/backplane.md) |
| **Branching** | Ref registry | Named divergent history lines, fork metadata | [Branching →](../concepts/branching.md) |
| **Comment** | Shared storage | Durable feedback, threaded replies, anchors | [Comment →](../concepts/comments.md) |
| **Comment Router** | One per server process | Owner-routing of comment mutations | [Comment Router →](../concepts/comment-router.md) |
| **Compaction** | Hot op log | Collapsing old ops into a snapshot | [Compaction →](../concepts/compaction.md) |
| **Document** | Logical unit | Id, type, state, revision counter | [Document →](../concepts/document.md) |
| **Document Router** | One per server process | Auth, ownership, routing, awareness | [Document Router →](../concepts/document-router.md) |
| **Draining** | Session lifecycle | Notifying the host of the final state when the last peer leaves | [Draining →](../concepts/session.md#draining-the-last-peer-leaves) |
| **Engine** | Per document type | How concurrent edits on a shape are merged | [Engines →](../engines/index.md) |
| **Health Checks** | Network edge | Liveness/readiness probes | [Health checks →](../operations/observability.md#health-checks) |
| **History** | Cold storage | Permanent log, milestones, time travel | [History →](../concepts/history.md) |
| **Merging** | Document engine | Integrating branches via 3-way transform | [Merging →](../concepts/merging.md) |
| **Multitenancy** | Core plumbing | Tenant isolation via id globalization | [Multi-tenancy →](../operations/multitenancy.md) |
| **Op** | In-flight / op log | A single atomic change to the document | [Engine contracts →](../reference/interfaces.md) |
| **Ownership** | Cluster coordination | Which node is authoritative for a document | [Ownership model →](../operations/backplane.md#ownership-model) |
| **Peer** | Per connection | One connected client, one peer id | [Peer →](../concepts/peer.md) |
| **Revision** | Per document | Monotonic counter; rebases concurrent ops | [Revision →](../concepts/revision.md) |
| **Session** | One per open doc, one node | In-memory state, apply lock, persistence | [Session →](../concepts/session.md) |
| **Snapshot** | Storage | Compact state checkpoint; shortens replay | [Snapshot →](../operations/snapshots.md) |
| **Storage** | External infra | Op log, snapshots, durable state | [Storage →](../storage/index.md) |
| **Transport** | Network edge | Wire protocol between clients and server | [Transport →](../transports/index.md) |
| **Versioning** | Ref registry | Immutable named pointers to revisions (tags) | [Versioning →](../concepts/versioning.md) |

---

## Anchor

An **anchor** ties a [comment](../concepts/comments.md) to a position in the document — an
offset range in text, a JSON path in structured data, and so on. Because the document keeps
changing underneath it, an anchor is **rebased automatically** after every edit and during
[compaction](../concepts/compaction.md), so it keeps pointing at the same logical content.
If the anchored region is deleted outright, the comment is marked *orphaned* rather than
silently lost. Anchor rebasing is engine-specific; OpStream ships anchor engines for text,
rich-text, and JSON. See [Anchors](../concepts/comments.md#anchors).

## Authorization

Two independent seams decide who may do what. The **collaboration** seam
(`IDocumentAuthorizer`) is consulted on every join / op / awareness call and defaults to
*allow-all* (with a startup warning) so you can prototype instantly. The **management**
seam (`IDatabaseCommandAuthorizer`) governs the control plane — list, delete, compact,
purge, plus all [branch](../concepts/branching.md)/[version](../concepts/versioning.md)/[merge](../concepts/merging.md)
operations — and defaults to *deny-all* (fail-closed). Production hosts replace **both**.
See [Authorization](../operations/authorization.md).

## Awareness

Presence / cursors / "user is typing" / live selections — **ephemeral** state that's
broadcast in real time but never persisted to the op log. Each [peer](../concepts/peer.md)
publishes their state via `UpdateAwarenessAsync`; the server keeps it for ~30 seconds, fans
it out to other peers, and drops it on disconnect or expiry. It is the volatile counterpart
to durable [comments](../concepts/comments.md). See [Awareness](../engines/awareness.md).

## Backplane

The cluster-wide pub/sub fabric. In single-node mode this is the in-process
`LocalBackplane`; for production you swap in `UseRedisBackplane()`. Its job is to ensure
that an [op](../reference/interfaces.md) applied on node A is broadcast to peers connected
to nodes B, C, …, and to coordinate which node [owns](../operations/backplane.md#ownership-model)
each document. See [Backplane](../operations/backplane.md).

## Branching

A **branch** is a named, independent line of edits derived from a parent document's state
at a specific [revision](../concepts/revision.md) — the *fork point*. Each branch has its
own physical op log and grows independently, exactly like a Git branch. You can fork at
HEAD or from a historical revision, and later [merge](../concepts/merging.md) branches back
together. See [Branching](../concepts/branching.md).

## Comment

A piece of collaborative feedback attached to a document at a specific point in its
timeline. Comments support threading (replies) and are [anchored](#anchor) to document
locations. Unlike awareness, they are **durable** — persisted to a dedicated comment store
and reachable over all three transports (including a streaming subscription). See
[Comment](../concepts/comments.md).

## Comment Router

The component that manages comment lifecycle operations. It routes every comment mutation
(create / edit / resolve / delete) to the node currently [owning](../operations/backplane.md#ownership-model)
the document, so the anchor can be fixed against the document's current revision atomically
under the [session](../concepts/session.md) lock. See [Comment Router](../concepts/comment-router.md).

## Compaction

**Compaction** keeps the hot op log from growing without bound by collapsing history up to
a chosen revision into a [snapshot](../operations/snapshots.md) and discarding the ops below
it. Open comment anchors are rebased first, and [version tags](../concepts/versioning.md)
pin a floor that compaction may never cross. See [Compaction](../concepts/compaction.md).

## Document

A **document** is whatever your application calls "the thing two users co-edit": a markdown
file, a Notion page, a settings dialog, a spreadsheet, a CAD model. OpStream is agnostic
about its semantics — it only needs you to pick an [engine](../engines/index.md) that knows
how to merge concurrent edits on its shape.

Each document has a **document id** (opaque, tenant-scoped), a **document type** (the
discriminator that selects the engine), a **state** of type `TDoc`, and a monotonic
**[revision](../concepts/revision.md)** counter. See [Document](../concepts/document.md).

## Document Router

The single entry point on the server side. Every transport funnels its join / op /
awareness calls through it, so authorization, [ownership](../operations/backplane.md#ownership-model),
routing, awareness fan-out, and idle cleanup all live in one place. You don't instantiate
it; `services.AddOpStream()` registers it and the transports resolve it from DI. See
[Document Router](../concepts/document-router.md).

## Draining

A document **drains** when its **last** [peer](../concepts/peer.md) disconnects — everyone
editing has left and it goes quiet. At that point OpStream takes a final
[snapshot](../operations/snapshots.md) and notifies any registered `IDocumentDrainHandler`
with the **complete final state** (id, type, revision, bytes), so the host can persist it to
its own database or trigger downstream work. The handler may also return a *delete* decision
to have OpStream permanently remove the document and all its data. Register one with
`AddDocumentDrainHandler<T>()`. See [Session → Draining](../concepts/session.md#draining-the-last-peer-leaves).

## Engine

An **engine** is the pluggable brain for one document **shape**. It implements
`IOpEngine<TDoc, TOp>` — applying, transforming, inverting, and composing ops — and decides
whether that type uses **Operational Transformation** (Text, Rich Text) or **CRDT**
semantics (JSON, Tree, Table, Form). Your code talks to the same contract either way; you
register one engine per document type with `AddEngine<…>(documentType)`. The engine is also
what powers [merging](../concepts/merging.md). See [Engines overview](../engines/index.md).

## Health Checks

Diagnostic endpoints that report the operational status of the server and its dependencies.
OpStream integrates with standard ASP.NET Core Health Checks to expose liveness and
readiness probes (e.g. verifying Redis connectivity or storage availability) for monitoring
and orchestration tools like Kubernetes. Probes are tagged `opstream`, `storage`, and
`backplane`. See [Health checks](../operations/observability.md#health-checks).

## History

Also known as **cold storage**, history is a permanent, append-only record of every
operation and important state checkpoint (milestone) in a document's lifecycle. While the
active op log may be [compacted](../concepts/compaction.md), the history store powers audit
logs, "time-travel" browsing of past versions, and restoration to specific revisions. It is
opt-in via `OpStreamOptions.History`. See [History](../concepts/history.md).

## Merging

**Merging** integrates changes from one branch into another. OpStream performs a **3-way
merge** using the fork point as the common ancestor. Rather than text diffs, the merge is
driven by the document's [engine](../engines/index.md): concurrent ops from the source
branch are **transformed** against the target's history. This makes merges deterministic,
intent-preserving, and identical in semantics to live collaboration. A dry-run mode previews
the result without committing. See [Merging](../concepts/merging.md).

## Multitenancy

The ability for a single OpStream cluster to serve multiple independent customers
(tenants). It uses **document id globalization** to isolate data: a document id sent by a
client is transparently scoped by the tenant's identity on the server, preventing
cross-tenant leaks. Clients only ever see their *local* id. See [Multi-tenancy](../operations/multitenancy.md).

## Op

An **op** is a single, atomic change to the document. The [engine](../engines/index.md)
knows how to `Apply` it (producing a new state), `Transform` it against a concurrent op so
its intent survives a rebase, `Invert` it (for undo), `Compose` two ops into one, and
recognize an `IsNoOp`. Ops are the unit that the [revision](../concepts/revision.md) counter
indexes and that [storage](../storage/index.md) persists. See [Engine contracts](../reference/interfaces.md).

## Ownership

In a multi-node cluster, exactly **one node owns each document at a time** — it hosts the
single live [session](../concepts/session.md), holds the apply lock, and arbitrates the
revision counter. The [Document Router](../concepts/document-router.md) transparently
proxies calls from non-owner nodes to the owner, and the [backplane](../operations/backplane.md)
coordinates handoff. Destructive management operations evict the owning session before
touching storage. See the [ownership model](../operations/backplane.md#ownership-model).

## Peer

A **peer** is a single connected client — typically one browser tab or one desktop/app
instance. Multiple peers under the same user account are treated as independent for
concurrency purposes; they're identified by a **peer id** the transport assigns at connect
time. Everything ephemeral — awareness, fan-out targeting, disconnect cleanup — is keyed by
it. See [Peer](../concepts/peer.md).

## Revision

The current revision is the index of the **last accepted op**. Clients include their
`baseRevision` with every op they send; if the server's revision is higher, the server
**rebases** the op through OT / CRDT transforms before applying it. This is what lets two
clients edit the same document without locks: each thinks it's editing revision N, and the
server reconciles. See [Revision](../concepts/revision.md).

## Session

A `DocumentSession<TDoc, TOp>` is the in-memory home of an open document on the server: it
owns the current state, holds the apply lock, talks to the store, and broadcasts via the
backplane. Sessions are created lazily when the first peer joins and closed after an idle
timeout (default 5 minutes); closing loses nothing, since state is rebuilt from storage on
the next join. See [Session](../concepts/session.md).

## Snapshot

To avoid re-applying every op from genesis on every load, OpStream takes **snapshots** —
compact serialized states tagged with their revision. At rehydration the session loads the
latest snapshot and replays only the ops after it. Snapshots are policy-driven (default:
every 100 ops or 5 minutes) and are the checkpoint that [compaction](../concepts/compaction.md)
collapses ops into. See [Snapshots](../operations/snapshots.md).

## Storage

**Storage** is where OpStream persists the op log and snapshots — the source of truth that
lets [sessions](../concepts/session.md) be rebuilt after a restart or failover. It sits
behind a single `IDocumentStore` interface; swap the provider and nothing else changes:

| Provider | Good for |
|---|---|
| `memory` | Local dev and tests — lost on restart |
| `sqlite` | Single-tenant edge boxes, small teams |
| `postgres` | Recommended default for new production projects |
| `mysql` | Existing MySQL / MariaDB shops |
| `sqlserver` | Microsoft stacks, Azure SQL |
| `mongo` | Document-heavy workloads, flexible schemas |
| `redis` | Lowest write latency, ephemeral or RDB-backed |

All EF Core providers run migrations on first connect — no manual `dotnet ef` step. Switch
provider with one env var:

```bash
OPSTREAM__STORAGE__PROVIDER=postgres
OPSTREAM__STORAGE__CONNECTIONSTRING="Host=...;Database=opstream;..."
```

See [Storage](../storage/index.md) for connection-string examples and per-backend tuning.

## Transport

A **transport** is the wire layer that connects clients to the OpStream server. OpStream
ships three; any combination can run on the same process and port simultaneously:

| Transport | Best for | Client requirement |
|---|---|---|
| **SignalR** | Browsers, .NET apps, mobile with a SignalR library | Official SignalR client (JS, .NET, Java, Swift, …) |
| **WebSocket** | Any stack — the most universal option | Native `WebSocket` in every browser and language |
| **gRPC** | Backend-to-backend, strongly-typed contracts | Generated gRPC stub (11+ languages) |

A React app on SignalR and a Python bot on WebSocket can collaborate on the same document
through the same server at the same time. The transport is invisible to the engine and
storage layers — it only carries ops and awareness in and fan-out out.

```bash
OPSTREAM__TRANSPORTS="signalr,websockets,grpc"
```

See [Transports](../transports/index.md) for per-transport configuration.

## Versioning

A **version** (or tag) is an immutable, named pointer to a specific revision in a
document's history. It lets you reliably "time-travel" to a past state and read the document
exactly as it was when the version was created. Versions are backed by
[history](../concepts/history.md) milestones so they remain accessible even after the active
op log is [compacted](../concepts/compaction.md) — and they pin a floor that protects that
data from purges. See [Versioning](../concepts/versioning.md).

## Next: [Transports →](../transports/index.md)
