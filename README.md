# OpStream

> A real-time collaborative editing toolkit for .NET applications.
> Bring Google-Docs-style co-editing to any document type your app
> already owns — without rewriting your auth, your storage, or your
> editor.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

**Status:** architecture design. No implementation yet. This document
is the contract this repository commits to deliver.

---

## Table of contents

1. [Motivation](#1-motivation)
2. [Design principles](#2-design-principles)
3. [Architecture overview](#3-architecture-overview)
4. [Core concepts](#4-core-concepts)
5. [Quick start](#5-quick-start)
6. [Subsystems](#6-subsystems)
7. [Editor adapters](#7-editor-adapters)
8. [Type safety across languages](#8-type-safety-across-languages)
9. [Configuration & DI](#9-configuration--di)
10. [Scalability](#10-scalability)
11. [Security](#11-security)
12. [Testing & the fuzz harness](#12-testing--the-fuzz-harness)
13. [Versioning & compatibility](#13-versioning--compatibility)
14. [Wire protocol](#14-wire-protocol)
15. [Package structure](#15-package-structure)
16. [End-to-end example](#16-end-to-end-example)
17. [Roadmap](#17-roadmap)
18. [Non-goals](#18-non-goals)
19. [Open questions](#19-open-questions)
20. [License](#20-license)
21. [Contributing](#21-contributing)

---

## 1. Motivation

Today, a .NET application that wants Google-Docs-style co-editing has
to reinvent most of the stack:

- A convergence algorithm (Operational Transformation or a CRDT).
  This is delicate work — subtle bugs (a caret that jumps, a duplicated
  insert) only show up under real load.
- A real-time transport with reconnection, backpressure, heartbeats,
  and message framing.
- Persistence (an in-memory dictionary fits a demo, not production).
- Presence, awareness, side-channel chat, anchored comments, version
  history, fine-grained permissions, sharding…
- And it must integrate with the host application's existing
  **authentication, authorization, and observability** — not bring
  its own parallel universe.

OpStream fills that gap. It ships sensible defaults so a first call
to `services.AddOpStream()` works out of the box, and clean extension
points so you can swap the engine, the storage, the transport, or the
authorization model when production demands it.

## 2. Design principles

1. **Pluggable, not monolithic.** Every subsystem (engine, transport,
   storage, auth, presence, observability) sits behind an interface.
   The default works; you extend it when you need to.
2. **Provable convergence.** The engine is validated by a *property-test*
   fuzz harness (random ops across N peers); any PR to an engine that
   doesn't converge is rejected.
3. **Zero magic.** No undocumented runtime reflection, no hidden
   background threads. Source generators are fine — they produce
   readable code at compile time.
4. **Backpressure everywhere.** A slow client never freezes peers or
   crashes the server.
5. **Tracing by default.** Every op flows through OpenTelemetry spans.
   You don't have to ask for it.
6. **DI-first.** Configuration via `Options<T>`, logging via
   `ILogger<T>`. Testable with a real `IServiceCollection`.
7. **No global state.** The singleton is not the library; the
   singleton is the `IDocumentRouter` the host registers.
8. **Strong types.** Ops aren't `JsonElement` blobs at the engine
   layer; they're `Op<TDoc>` with generic transform/apply.
9. **Small and fast by default, scalable on demand.** You decide when
   to enable Redis, sharding, or cold storage.
10. **Editor-agnostic.** Adapters for `contenteditable`, Monaco,
    CodeMirror, ProseMirror, TipTap; the wire protocol is documented
    so any client (React, Vue, MAUI, native mobile) can speak it.

## 3. Architecture overview

```
┌────────────────────────────────────────────────────────────────┐
│                       Host ASP.NET Core app                     │
│                                                                 │
│  ┌──────────────┐    ┌──────────────┐    ┌─────────────────┐   │
│  │ Auth         │    │ Authz        │    │ Telemetry sinks │   │
│  │ (Identity)   │    │ (policies)   │    │ (OTel exporter) │   │
│  └──────┬───────┘    └──────┬───────┘    └────────┬────────┘   │
│         │                   │                     │            │
│  ┌──────▼───────────────────▼─────────────────────▼────────┐   │
│  │                    OpStream (library)                   │   │
│  │                                                          │   │
│  │  Transport layer ──► Hub adapters (SignalR / WS / gRPC) │   │
│  │  Session layer ────► IDocumentSession, presence, chat   │   │
│  │  Engine layer  ────► OT engines, CRDT engines, plugins  │   │
│  │  Storage layer ────► IDocumentStore (Memory/Redis/SQL…) │   │
│  │  Cluster layer ────► IBackplane (no-op / Redis / NATS)  │   │
│  └──────────────┬─────────────────────┬─────────────────────┘   │
│                 │                     │                          │
└─────────────────┼─────────────────────┼──────────────────────────┘
                  │                     │
              ┌───▼───┐             ┌───▼────────┐
              │ Redis │             │ SQL Server │
              │ Postgres│           │  (op log)  │
              └───────┘             └────────────┘
```

Four layers, from bottom to top:

- **Engine** — the math. Defines `IOpEngine<TDoc, TOp>` with
  `Transform`, `Apply`, `Compose`, `Invert`. Built-in
  implementations: `TextOtEngine`, `JsonCrdtEngine`,
  `ListCrdtEngine`, `RichTextEngine` (Quill-Delta-style ops).
- **Storage** — what is persisted and how. Periodic snapshots plus
  the op log since the last snapshot. Interface `IDocumentStore`.
- **Session** — the runtime heart. For each open document, an
  `IDocumentSession` groups peers, holds the current revision,
  applies ops (validating them), and broadcasts presence and chat.
- **Transport** — packages messages for SignalR, raw WebSocket, or
  gRPC streaming. The transport is only an adapter; all the logic
  lives in the Session layer.

## 4. Core concepts

| Concept | What it is | Notes |
|---|---|---|
| `DocumentId` | Opaque identifier | Any string; chosen by the host |
| `DocumentType` | Type tag | `"text"`, `"rich-html"`, `"json"`, custom |
| `Revision` | Monotonic integer | Every accepted op increments by 1 |
| `Op<TDoc>` | Applicable, transformable change | Insert / Delete / etc. |
| `Snapshot<TDoc>` | Dense state at a revision | For fast cold-start |
| `Session` | Runtime state for one document | In memory on the owning node |
| `Peer` | An active connection on a document | `ConnectionId`, `UserId`… |
| `Awareness` | Ephemeral peer data | Cursor, selection, color, typing |
| `Comment` | Annotation anchored to a range | Survives ops via rebase |
| `Branch` | Named branch of a document | Optional, for editorial flows |

A peer always sees a consistent view: its client revision may lag
behind the server, but when remote ops arrive they are transformed
against the local pending ops, and the user's edits are never lost.

## 5. Quick start

```csharp
// Program.cs in the host application
builder.Services.AddOpStream(o =>
{
    o.UseEngine<RichTextEngine>();              // formatted text
    o.UseStorage<RedisDocumentStore>(r =>       // snapshots + op log
    {
        r.ConnectionString = builder.Configuration["Redis"];
        r.SnapshotEvery = TimeSpan.FromMinutes(2);
        r.OpLogRetention = TimeSpan.FromDays(7);
    });
    o.UseBackplane<RedisBackplane>();           // multi-node
    o.UseTransport<SignalRTransport>(s =>
    {
        s.HubPath = "/hubs/collab";
        s.MaxMessageSize = 64 * 1024;
    });
    o.UseAuthorization<MyDocumentAuthorizer>(); // host integration
});

app.MapOpStream();                              // mounts the hub
```

Plug into the host's existing permission model:

```csharp
public class MyDocumentAuthorizer : IDocumentAuthorizer
{
    private readonly IAuthorizationService _authz;

    public MyDocumentAuthorizer(IAuthorizationService authz) => _authz = authz;

    public async Task<DocumentAccess> AuthorizeAsync(
        ClaimsPrincipal user, string documentId, CancellationToken ct)
    {
        if (await _authz.HasPolicy(user, "doc.write", documentId))
            return DocumentAccess.ReadWrite();
        if (await _authz.HasPolicy(user, "doc.read", documentId))
            return DocumentAccess.ReadOnly();
        return DocumentAccess.Denied;
    }
}
```

On the client (Blazor):

```razor
<CollabEditor TDoc="RichTextDocument"
              DocumentId="@($"pagecontent-{Id}")"
              Adapter="@(new ContentEditableAdapter("#editor"))"
              UserDisplayName="@User?.Name" />
```

The adapter bridges the `IDocumentSession` and the concrete editor
(contenteditable, Monaco, etc.). That contract is what turns
"collaborative editing for any document type" from marketing into
truth.

## 6. Subsystems

### 6.1 Engines (the convergence algorithm)

```csharp
public interface IOpEngine<TDoc, TOp>
{
    TDoc Apply(TDoc state, TOp op);
    TOp? Transform(TOp incoming, TOp existing, OpPriority priority);
    TOp? Compose(TOp a, TOp b);          // null if not composable
    TOp Invert(TOp op, TDoc preState);   // for undo
    bool IsNoOp(TOp op);
}
```

Built-in implementations:

| Engine | Algorithm | Use case |
|---|---|---|
| `TextOtEngine` | Jupiter OT (insert/delete) | Plain text, code, serialized HTML |
| `RichTextEngine` | Quill-Delta (retain/insert/delete + attrs) | Formatted text |
| `JsonCrdtEngine` | Automerge-inspired | Hierarchical objects/arrays |
| `ListCrdtEngine` | RGA (Replicated Growable Array) | Strongly-ordered lists |
| `CounterCrdtEngine` | PN-Counter | Counters, votes, likes |

Users can register custom engines. OpStream **guarantees convergence**
through the fuzz harness: if `Transform` and `Apply` don't satisfy
TP1/TP2 (for OT) or commutativity (for CRDT), the test fails.

### 6.2 Storage (`IDocumentStore`)

```csharp
public interface IDocumentStore
{
    Task<DocumentSnapshot?> LoadSnapshotAsync(
        string docId, CancellationToken ct);

    IAsyncEnumerable<StoredOp> StreamOpsAsync(
        string docId, long sinceRevision, CancellationToken ct);

    Task AppendOpAsync(string docId, StoredOp op, CancellationToken ct);
    Task WriteSnapshotAsync(string docId, DocumentSnapshot snapshot, CancellationToken ct);
    Task CompactAsync(string docId, long upToRevision, CancellationToken ct);
}
```

Official providers:

- **`MemoryDocumentStore`** (default) — for dev, tests, and demos.
  Lock-free, no durability. Registered automatically by `AddOpStream()`;
  call `UseMemoryStorage()` to be explicit.
  ```csharp
  .UseMemoryStorage()     // ⚠ not for production
  ```

- **`RedisDocumentStore`** — snapshot in `STRING`, op log in `STREAM`,
  pruning via `XTRIM`. Cluster-aware.
  ```csharp
  .UseRedisStorage(o =>
  {
      o.ConnectionString = builder.Configuration["Redis"];
      o.SnapshotEvery    = TimeSpan.FromMinutes(2);
      o.OpLogRetention   = TimeSpan.FromDays(7);
  })
  ```

- **`MongoDocumentStore`** — document snapshots and op log stored in
  MongoDB collections.
  ```csharp
  .UseMongoDbStorage(connectionString, databaseName: "opstream")
  ```

- **`EfCoreDocumentStore`** — relational storage via Entity Framework Core.
  Four tables: `DocumentSnapshots`, `DocumentOps`, `HistorySnapshots`,
  `HistoryOps`.

  **Important — EF Core migrations are provider-specific.** A single
  migration cannot target multiple databases because EF Core encodes
  provider column types (e.g. `TEXT`/`BLOB` for SQLite vs. `nvarchar`/
  `varbinary` for SQL Server) at design time. For this reason OpStream
  ships a **thin package per provider**, each containing only:
  a one-line `DbContext` subclass and a `Migrations/` folder with the
  provider-specific SQL. All business logic stays in the shared
  `EfCoreDocumentStore<TContext>` class.

  | Provider | Package | Extension method |
  |---|---|---|
  | SQL Server | `OpStream.Server.Storage.SqlServer` | `UseSqlServerStorage(connStr)` |
  | PostgreSQL | `OpStream.Server.Storage.PostgreSQL` | `UsePostgreSqlStorage(connStr)` |
  | SQLite | `OpStream.Server.Storage.SQLite` | `UseSqliteStorage(connStr)` |
  | MySQL / MariaDB | `OpStream.Server.Storage.MySQL` | `UseMySqlStorage(connStr)` |

  ```csharp
  // SQL Server example
  .UseSqlServerStorage(builder.Configuration.GetConnectionString("OpStream"))

  // PostgreSQL example
  .UsePostgreSqlStorage(builder.Configuration.GetConnectionString("OpStream"))
  ```

  If you need to customise the schema or co-locate OpStream tables inside
  your own `DbContext`, inherit from the provider's `OpStreamDbContext`
  subclass and point `MigrationsAssembly` at your assembly.

- **`FileSystemDocumentStore`** — useful for air-gapped environments;
  append-only log plus snapshot files. *(planned — not yet implemented)*

Each provider will expose OpenTelemetry counters (`opstream.store.read_ms`,
`opstream.store.append_ms`, etc.) and built-in health checks in a future
release.

### 6.3 Transports (`ITransport`)

- **`SignalRTransport`** — the default. Reuses the host's existing
  `HubRouteHandler` and respects auth middleware.
- **`WebSocketTransport`** — for clients without a SignalR library
  (mobile, embedded).
- **`GrpcStreamingTransport`** — for gateways or service-to-service
  .NET scenarios.
- **`LocalTransport`** — in-process; zero network, useful for fast
  end-to-end tests.

The wire protocol is documented so any third-party client can
implement it. See [§14](#14-wire-protocol).

### 6.4 Backplane (multi-node)

```csharp
public interface IBackplane
{
    Task<IAsyncDisposable> SubscribeAsync(
        string docId,
        Func<NodeMessage, ValueTask> handler,
        CancellationToken ct);

    Task PublishAsync(string docId, NodeMessage msg, CancellationToken ct);
}
```

Without a backplane (`LocalBackplane`, the default), peers connected
to the same node still see each other through in-process fan-out —
fine for single-instance deployments. With `RedisBackplane`
or `NatsBackplane`, a document can have peers connected to different
servers and still converge.

A session lives on **one node at a time** (document ownership chosen
via consistent hashing). Other nodes forward ops to the owner.
This avoids "OT across servers", which is where CRDTs win: if the
user picks `JsonCrdtEngine`, OpStream can allow shared ownership
(each node applies locally and syncs through the backplane).

### 6.5 Authorization (`IDocumentAuthorizer`)

Deliberately does **not** bring its own identity system. Receives the
`ClaimsPrincipal` the host already has on the `HubCallerContext`
plus a `DocumentId`, and returns a `DocumentAccess`:

```csharp
public readonly record struct DocumentAccess(
    bool CanRead,
    bool CanWrite,
    bool CanComment,
    bool CanManagePresence,
    bool CanChat,
    ImmutableHashSet<string>? RestrictedRegions);
```

`RestrictedRegions` enables rules like "this user can edit the body
but not the header", delegating to the engine how regions map to
document coordinates.

OpStream also exposes per-op hooks:

```csharp
public interface IOpValidator<TOp>
{
    ValueTask<ValidationResult> ValidateAsync(
        OpContext<TOp> ctx, CancellationToken ct);
}
```

`OpContext` carries user, document, op, and pre-state. Useful for
rules like "reject deletes larger than N characters", "block ops
containing banned words", or "per-user rate limiting".

### 6.6 Observability

OpenTelemetry out of the box. No extra configuration needed beyond
the one-liner `builder.AddOpStreamTelemetry()` (in the
`OpStream.Aspire` package), which registers the `Meter("OpStream")`
and `ActivitySource("OpStream")` on whatever OTel pipeline the host
already has (Aspire `ServiceDefaults`, plain ASP.NET Core, console
exporter, …).

#### Traces

Every applied op produces this span hierarchy, child of the inbound
HTTP / WS / gRPC request span:

```
opstream.session.apply_op            ← top-level, tagged with
  │                                     doc.id, peer.id, op.size,
  │                                     op.base_revision, engine.name
  ├── opstream.engine.transform      ← only present if rebasing occurred
  │                                     tagged with transforms = N
  ├── opstream.engine.apply
  ├── opstream.store.append          ← tagged with revision
  └── opstream.backplane.publish     ← tagged with revision
```

Activity tags propagate automatically to APM tools (Application
Insights, Datadog, Honeycomb, …) because every span uses the
standard `System.Diagnostics.ActivitySource`.

#### Metrics (Meter `OpStream`)

| Instrument | Kind | Unit | What it measures |
|---|---|---|---|
| `opstream.active_documents` | UpDownCounter | `{documents}` | Documents currently in memory on this node |
| `opstream.ops_processed_total` | Counter | `{operations}` | Total successfully applied ops (divide by interval for ops/s) |
| `opstream.ops_rejected_total` | Counter | `{operations}` | Ops rejected by validators, authorization, or protocol |
| `opstream.op_apply_latency_ms` | Histogram | ms | End-to-end latency of `ApplyOpAsync` |
| `opstream.transform_count_per_op` | Histogram | `{transforms}` | How many concurrent ops were rebased before apply |
| `opstream.broadcast_fanout` | Histogram | `{peers}` | Peers notified per broadcast (local set) |
| `opstream.peers_per_document` | Histogram | `{peers}` | Active peers per document, sampled on every applied op |
| `opstream.store.append_latency_ms` | Histogram | ms | Latency of `IDocumentStore.AppendOpAsync` |
| `opstream.store.read_latency_ms` | Histogram | ms | Latency of reads against the store |
| `opstream.backplane.publish_latency_ms` | Histogram | ms | Latency of `IBackplane.PublishAsync` |

#### Structured logs

`ILogger<DocumentSession>` automatically opens a scope with
`(doc.id, peer.id)` so every line emitted while applying an op
carries both fields. Levels follow the conventional ladder:

| Level | When |
|---|---|
| `DEBUG` | Per op — payload size, base revision, transform count |
| `INFO`  | Per join / leave / session close |
| `WARN`  | Op rejected (validator, compacted log, missing factory) |
| `ERROR` | Exception applying an op or in backplane request handler |

At startup the `DocumentRouter` also logs the registered storage,
backplane, and engines — and emits a `WARN` if the in-memory store is
still active (i.e. nobody called `UseRedisStorage()` or similar).

#### Diagnostics endpoint

Wire it via `OpStream.Aspire`:

```csharp
app.MapOpStreamDiagnostics(
    basePath: "/opstream/diag",
    authorizationPolicy: "OpStreamDiagnostics", // gate it in production
    recentOpCount: 50);
```

`GET /opstream/diag/{docId}` returns a `DocumentDiagnostics` JSON
document with `Revision`, `PeerCount`, `Peers[]`, `OwnerNodeId`,
`ActiveOnThisNode`, and the tail of the op log.

#### Health checks

`AddOpStream()` registers two checks:

- `opstream-storage` — degraded when `MemoryDocumentStore` is still
  active (data is lost on restart). When `UseRedisStorage()` is
  called, this check is replaced with a Redis ping (unhealthy on
  connection loss).
- `opstream-backplane` — healthy for the default single-node
  `LocalBackplane`. When `UseRedisBackplane()` is called, this check
  is replaced with a Redis ping.

Expose them with the standard ASP.NET helper:

```csharp
app.MapHealthChecks("/health");
```

#### `EventCounter` for tools predating OTel

The library also publishes a classic `EventSource` named `OpStream`
with the most common counters (`ops-applied-per-second`,
`ops-rejected-per-second`, `active-documents`, plus per-histogram
samples). Useful for `dotnet-counters`, PerfView, or Visual Studio's
Diagnostic Tools:

```
dotnet-counters monitor --name MyApp OpStream
```

#### Aspire integration

Two packages:

- **`OpStream.Aspire`** — service-side integration:
  - `builder.AddOpStreamTelemetry()` registers the `Meter` and
    `ActivitySource` on the OpenTelemetry pipeline (the same one
    `ServiceDefaults` configures).
  - `app.MapOpStreamDiagnostics(...)` mounts the diagnostics endpoint.
- **`OpStream.Hosting.Aspire`** — AppHost-side integration. Currently
  ships canonical resource names (`OpStreamResourceNames.Redis`,
  `OpStreamResourceNames.RelationalDatabase`) so AppHost code and
  service code agree on a single source of truth. Strongly-typed
  AppHost extensions land once the Aspire SDK API stabilises.

In an Aspire-enabled host the dashboard automatically shows the latency
histograms, the complete waterfall for a single op (HTTP →
`opstream.session.apply_op` → `opstream.engine.transform` →
`opstream.store.append` → `Redis XADD`), and the health-check verdicts.

### 6.7 Presence & awareness

A separate channel from ops — higher frequency, lower durability.
Each peer publishes an `AwarenessState`. OpStream includes canonical
fields (`cursor`, `selection`, `color`, `typing`) and lets you extend
through a `JsonElement Extras`.

- Default throttle: one emission every 60 ms per peer.
- Auto-expire: a peer that hasn't updated in 30 s is considered idle.

### 6.8 Chat

A side pub/sub channel, optionally persistable via a separate
`IChatStore`. Works without persistence (everything in client memory)
or with per-document history.

### 6.9 Comments & suggestions

Comments are anchored to a document range. Whenever the document
mutates, the anchors rebase automatically through the engine (the
`Transform` operation is applied to the comment's `(start, end)`).
Threads, resolved/unresolved, mentions — all built in.

Suggestions (Google-Docs-style "suggesting mode") are ops applied to
a non-canonical shadow document; they require acceptance to merge.
Built on top of branches.

### 6.10 History & versioning

- **Linear** — every op sits in the log with its revision.
- **Labeled snapshots** — the host marks named points ("Published v3",
  "Before redesign"). Recoverable.
- **Diff** between two revisions (plain text or structured, depending
  on the engine).
- **Undo/redo** in two modes: global, or `UserScopedUndo` which only
  inverts ops authored by your `UserId`.

### 6.11 Branching & merge

Optional, requires an engine that supports it. Useful for editorial
flows (a "draft" branch vs. "live"). Merge uses three-way OT/CRDT
with a resolution policy the host configures.

### 6.12 Offline support

The client may keep editing offline: ops accumulate in local storage
(IndexedDB in browsers, SQLite in .NET). On reconnect, the client
ships its ops with a stale `base_revision`; the server transforms
and applies them. If transformation fails business validation, the
library returns a `RejectionEnvelope` so the client can decide
(show a diff, discard, etc.).

### 6.13 Attachments

Outside the main document. `IAttachmentStore` accepts blob uploads
(images, video) and returns a stable URL. Ops reference the
attachment id; deduplication by content hash.

### 6.14 Region-level permissions

As mentioned, `DocumentAccess.RestrictedRegions` is checked **before**
`Apply`. If an op falls in a protected region the server replies
`Rejected("RegionLocked")`.

### 6.15 End-to-end encryption (optional)

A mode where the server only orders ops without reading them (ops
arrive encrypted with a key shared between peers). It trades some
features (no server-side rebase, no validation hooks, no server-side
diff) for real privacy.

## 7. Editor adapters

"Low effort to edit any kind of document" becomes concrete through
these official client-side adapters:

| Editor | Adapter | Document type |
|---|---|---|
| Vanilla `contenteditable` | `ContentEditableAdapter` | `RichTextDocument` |
| Monaco | `MonacoAdapter` | `TextDocument` (code) |
| CodeMirror 6 | `CodeMirrorAdapter` | `TextDocument` |
| ProseMirror | `ProseMirrorAdapter` | `RichTextDocument` |
| TipTap | `TipTapAdapter` | `RichTextDocument` |
| Plain `<input>` / `<textarea>` | `InputAdapter` | `TextDocument` |
| Complex form | `JsonFormAdapter` | `JsonDocument` |
| Map / canvas / Figma-like | (`IEditorAdapter` documented) | custom |

The adapter maps local events to engine ops and remote ops to editor
mutations while preserving caret/selection — the part that hurts the
most when you try to do it by hand.

## 8. Type safety across languages

A real-world OpStream deployment likely has JavaScript or TypeScript
clients (and possibly Swift, Kotlin, or Dart). The library is
written in C#, so we need a strategy that keeps everyone in sync
without sacrificing developer experience on either side.

OpStream addresses this in three layers:

**Layer 1 — Standard engines need no codegen.**
If the user picks `TextOtEngine` (plain text) or `RichTextEngine`
(Quill-Delta), the op structure is an industry de-facto standard.
A Quill / TipTap "Delta" in JavaScript always looks the same:
`[{ retain: 5 }, { insert: "Hello" }]`. The TypeScript community
already publishes types for these (e.g. `@types/quill`). The .NET
side serializes/deserializes to the same JSON. The wire protocol is
a public, agnostic, immutable contract.

**Layer 2 — Source generators for custom state.**
Where the synchronization burden bites is in:
- `JsonCrdtEngine` documents (e.g. a kanban board, a complex form).
- Custom `AwarenessState` (e.g. a domain-specific `UserRole` enum,
  app-specific cursor coordinates).

Here, source generators are the right tool. An optional
`OpStream.TypeScript.Generator` NuGet package reads attributes:

```csharp
[CollabState(ExportTypeScript = true)]
public record KanbanBoard(
    string Id,
    ImmutableList<KanbanColumn> Columns);

[CollabAwareness]
public record MyPresence(
    string CursorNodeId,
    int CaretOffset,
    string AvatarColor);
```

At compile time the generator:

1. Emits AOT-friendly `System.Text.Json` serializers inside the .NET
   assembly.
2. Writes a `opstream-types.ts` file to a path configured in the
   `.csproj` (e.g. `../frontend/src/types/`).

The generated TypeScript:

```typescript
// Auto-generated by OpStream. Do not edit.
export interface KanbanBoard {
    id: string;
    columns: KanbanColumn[];
}

export interface MyPresence {
    cursorNodeId: string;
    caretOffset: number;
    avatarColor: string;
}
```

Result: a single source of truth (.NET wins), strong autocomplete on
both sides, zero runtime overhead.

**Layer 3 — JSON Schema for non-TypeScript clients.**
The generator's intermediate form is **JSON Schema**, not TypeScript
directly. From there, language-specific generators emit `.ts`, and
third-party tools (QuickType, swift-protobuf, etc.) emit Swift,
Kotlin, or Dart. The same schema is exposed at
`GET /opstream/_schema/{docType}` for development.

**The core stays strongly typed.** Inside C#, everything is generic
(`Op<TDoc>`, `IOpEngine<TDoc, TOp>`). Dynamic typing only appears at
the transport frontier: a payload arrives as `JsonElement`, the
registered engine deserializes it into its strong `TOp`, and from
that point on everything is type-checked again.

## 9. Configuration & DI

Everything is configured with the Options pattern. Per-document-type
overrides for multi-tenant or multi-type applications:

```csharp
services.AddOpStream()
    .ConfigureDocumentType<RichTextDocument>(o =>
    {
        o.Engine = new RichTextEngine();
        o.SnapshotEvery = TimeSpan.FromMinutes(5);
        o.MaxPeersPerDocument = 50;
    })
    .ConfigureDocumentType<TextDocument>(o =>
    {
        o.Engine = new TextOtEngine();
        o.SnapshotEvery = TimeSpan.FromMinutes(15);
        o.UseCompactingOpLog = true;
    });
```

## 10. Scalability

- **Shard by document.** Consistent hashing on `DocumentId` picks
  the owning node. Drain + handoff on rebalance.
- **Backpressure per peer.** If the outbound buffer for a client
  exceeds X bytes, the server starts dropping awareness updates (not
  ops) until it drains. After N seconds, the client is kicked
  (auto-reconnect picks it up).
- **Op batching.** Awareness in 50 ms bursts; ops are never batched
  (latency matters more). Configurable per-peer.
- **Cold storage.** Documents with no peers are evicted from RAM
  after N minutes; on return, snapshot + tail from storage.
- **Read-only replication.** View-only clients can connect to
  replicas that receive snapshots through the backplane without
  participating in OT.
- **Pure-managed hot paths with SIMD.** Mass diffing (a paste of an
  entire document) and CRDT tombstone compaction are the two places
  CPU bites. Both are solved with `System.Runtime.Intrinsics`
  (`Vector256<T>`), which the JIT/AOT lowers to AVX2/AVX-512 on
  x86 and NEON on ARM64. No need to drop down to C++ or P/Invoke —
  this keeps the package trim-friendly and portable (Linux ARM64,
  Apple Silicon, FreeBSD, WebAssembly).

## 11. Security

- **Chat sanitization** (anti-XSS) built in, opt-out.
- **Payload limits** per op, per chat message, per presence frame.
- **Rate limits** per user (token bucket).
- **Audit trail** via `IAuditSink`: each op produces
  `(user, doc, op, timestamp)` for downstream SIEM ingestion.
- **Content scanning hook** (`IContentScanner`) for DLP / antivirus
  integration on attachments.
- **Tenant isolation** through `IDocumentRouter`: tenants never share
  a `DocumentId`.

## 12. Testing & the fuzz harness

OpStream ships a public test toolkit:

```csharp
[Test]
public async Task ConcurrentEditsConverge()
{
    using var harness = OpStream.TestHarness.Create<RichTextDocument>();
    var alice = harness.AddPeer("alice");
    var bob = harness.AddPeer("bob");

    await alice.OpenAsync("doc-1");
    await bob.OpenAsync("doc-1");

    await alice.ApplyAsync(Insert(0, "Hello "));
    await bob.ApplyAsync(Insert(0, "World "));

    await harness.QuiesceAsync();

    alice.Document.PlainText.Should().Be(bob.Document.PlainText);
}
```

And the **fuzz harness**, which is mandatory in CI for any change
that touches an engine: it generates random sequences of ops over
N peers with simulated network conditions (delay, reordering,
partition) and asserts that all peers converge to the same state.

## 13. Versioning & compatibility

OpStream touches three surfaces that can break independently and
need different policies. Confusing them is the recipe for losing
user trust.

### 13.1 The three surfaces

| Surface | What it is | Who notices | Severity |
|---|---|---|---|
| **API (.NET)** | Public types, methods, interfaces | Anyone compiling against the package | Compile-time |
| **Wire protocol** | JSON/Protobuf between client and server | Any deployed client | Runtime |
| **Storage format** | How ops and snapshots are persisted | Anyone with stored data | Data at rest |

### 13.2 SemVer and the Tick-Tock cadence

- **`x.y.Z` — Patch.** Bug fixes and performance improvements only.
  No risk. Safe to upgrade in production unattended.
- **`x.Y.0` — Minor.** New features; 100% backward compatible in
  source and binary. Anything slated for removal is marked
  `[Obsolete(error: false)]` with a message pointing to the
  replacement. Interfaces evolve via Default Interface Methods (DIM)
  so implementers don't break.
- **`X.0.0` — Major.** Obsolete code is removed. An upgrade guide is
  mandatory. A version-`X` server keeps speaking the wire protocol
  of version `X-1` for at least one cycle so customers can roll
  deployments incrementally.

### 13.3 Wire protocol rules

**Does NOT bump** the protocol version (old clients simply ignore
what they don't understand):
- Adding optional properties to an `Op` or to a presence/chat message.
- Adding new message types (`type: "chat_reaction"`) provided they
  don't affect document convergence.

**DOES bump** the protocol version (old clients would diverge or
break):
- Changing the engine algorithm or its indexing unit (UTF-16 →
  code points, say).
- Changing conflict-resolution semantics.
- Renaming or removing mandatory fields (`docId`, `revision`,
  `payload`, `proto`).
- Changing how op batches are packed.

When the backend accepts two versions, it logs a periodic
`LogWarning`: *"detected client on proto v1; support removed in
OpStream v3.0"*, so operations teams know they have outstanding
debt.

### 13.4 Storage format rules

**Old ops on disk never expire.** Even if the library is on v3.0,
the store must be able to read ops written on v1.0. This is achieved
with:

- A `SchemaVersion` field on `StoredOp`.
- Registered `IOpUpcaster` instances that rewrite old ops into the
  current format on read, **before** the engine ever sees them.
- For unavoidable destructive rewrites: a documented
  `OpStreamMigration` CLI / `await MigrateAsync()` method — never
  silent.

### 13.5 Tooling to smooth the edges

- **Roslyn analyzer** shipped in the main package: when an API is
  deprecated, the IDE offers an "Upgrade to the new OpStream API"
  code fix — one click rewrites the call site. Turns a breaking
  change into a three-second experience.
- **`[Obsolete]` with actionable messages** always: states the
  replacement API and the version where it disappears.
- **Wire compatibility tests** in CI: the repo stores payload
  fixtures from every historical version and verifies the current
  server still accepts them.

### 13.6 Public stability promise

> OpStream follows strict SemVer on its .NET API. Every minor
> release keeps the wire protocol backward-compatible. The storage
> format is backward-compatible across an entire major line.
> Deprecated features live for at least one full minor cycle with
> `[Obsolete]` and a Roslyn code fix before removal.

## 14. Wire protocol

### 14.1 Connection handshake

The protocol version is negotiated in the application layer (the
first `Join` message), not via an HTTP header — manipulating headers
on WebSocket upgrades is awkward in some SignalR/WS clients.

The flow:

1. Client connects to the transport (SignalR, raw WS, …).
2. Client sends its first mandatory message:
   ```json
   { "type": "join", "docId": "doc-1", "proto": 1, "...": "..." }
   ```
3. The server evaluates. If the version is supported, it responds:
   ```json
   { "type": "joined", "revision": 42, "proto": 1, "...": "..." }
   ```
4. If the version is **not** supported, the server aborts with a
   formal error and closes the connection:
   ```json
   { "type": "error", "code": "UnsupportedProtocol", "min": 2, "max": 3 }
   ```

The client surfaces `UnsupportedProtocol` to the application so it
can show a "please reload" banner instead of silently failing.

### 14.2 Server version range

The server doesn't enforce a single strict version — it advertises
`[MinSupportedVersion, MaxSupportedVersion]`.

```csharp
public static class ProtocolVersions
{
    public const int Current = 2;
    public const int MinSupported = 1;
}
```

If the server supports `v1` and `v2`, and a client connects with
`v1`, the transport instantiates an `IProtocolTranslator` for that
connection. The rest of the system (Session, Engine, Store) always
works with the latest version in memory; the transport translates
inbound and outbound messages for the specific client. This keeps
`if (version == 1)` branches out of business logic.

### 14.3 Payload shape

Messages are JSON (Protobuf as an alternative encoding) and follow a
discriminated-union shape. Example op envelope:

```json
{
  "type": "op",
  "docId": "doc-123",
  "revision": 42,
  "engine": "rich-text",
  "payload": [ { "insert": "hello" } ]
}
```

The `engine` field selects which `IOpEngine` deserializes
`payload`. Until that point the payload travels as `JsonElement` /
`ReadOnlySpan<byte>` — typed deserialization happens at the engine
boundary, not at the transport.

The full protocol specification lives in `docs/protocol-v1.md`
(future) with JSON Schema for every message.

## 15. Package structure

OpStream is intentionally modular. Inspired by OpenTelemetry,
MassTransit, and Serilog, the goal is **dependency hygiene**: a user
who needs EF Core and gRPC shouldn't be forced to download
`StackExchange.Redis`.

### 15.1 Why modular

- **Avoid bloat.** Each storage / transport pulls only its own
  dependencies.
- **Framework separation.** The core package targets pure `net8.0+`
  without an `ASP.NET Core` framework reference, so sessions can be
  hosted in a Worker Service, an Orleans grain, or a unit test
  without standing up an HTTP host. SignalR support is an opt-in
  package that carries the `Microsoft.AspNetCore.App` framework
  reference.
- **AOT / trimming friendliness.** Fewer third-party dependencies in
  the core means easier compatibility with AOT compilation.
- **Independent versioning.** A breaking change in
  `StackExchange.Redis 3.0` only bumps `OpStream.Storage.Redis`; the
  core (which exposes only `IDocumentStore`) stays at its own
  version.

### 15.2 Package layout

```
OpStream                                # core, no infra dependencies
  ├── Engine.RichText                   # Quill-Delta engine
  ├── Engine.JsonCrdt                   # Automerge-style engine
  ├── AspNetCore.SignalR                # transport
  ├── Transport.Grpc                    # transport (future)
  ├── Storage.EntityFrameworkCore       # storage
  ├── Storage.Redis                     # storage + can act as backplane
  ├── Backplane.Redis                   # multi-node fan-out
  ├── Backplane.Nats                    # multi-node fan-out
  ├── Hosting.Aspire                    # AppHost extensions
  ├── Aspire                            # client telemetry registration
  ├── TestHarness                       # public test utilities
  └── TypeScript.Generator              # source generator → .ts / JSON Schema
```

### 15.3 Discoverability without sprawl

The risk of many packages is the user not knowing what to install.
Two mitigations:

**Typed builder methods.** The DI surface uses an
`IOpStreamBuilder` interface; extension methods like `UseRedisStorage`
or `UseSignalRTransport` only show up in autocomplete when the
corresponding package is installed. The user is guided by the IDE.

```csharp
public interface IOpStreamBuilder { }

// Defined in OpStream.Storage.Redis
public static IOpStreamBuilder UseRedisStorage(
    this IOpStreamBuilder builder, string connectionString) { … }
```

**Templates.** `dotnet new opstream` scaffolds a starting solution
with the common packages already wired.

### 15.4 Internal tooling

- **Central Package Management** (`Directory.Packages.props`) keeps
  every sub-package on the same version of every shared dependency.
- **MinVer** so all `OpStream.*` packages share the same semantic
  version when published.

## 16. End-to-end example

A complete Blazor application using OpStream with Aspire:

```csharp
// AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("opstream-redis");
var db = builder.AddPostgres("opstream-db").AddDatabase("OpStreamStore");

var api = builder.AddProject<Projects.MyWebApp>("webapi")
                 .WithReference(redis)
                 .WithReference(db);

builder.Build().Run();
```

```csharp
// MyWebApp/Program.cs
builder.AddOpStream(options =>
{
    options.UseEngine<RichTextEngine>();
    options.UseRedisBackplane(
        builder.Configuration.GetConnectionString("opstream-redis"));
    options.UseHybridStorage(h =>
    {
        h.Hot.UseRedis(builder.Configuration.GetConnectionString("opstream-redis"));
        h.Cold.UseEfCore<AppDbContext>();
    });
    options.UseAuthorization<MyDocumentAuthorizer>();
});

// Aspire telemetry — meters and activity sources are registered on
// the OTel pipeline configured by ServiceDefaults.
builder.AddOpStreamTelemetry();
```

```razor
@* MyWebApp/Pages/Editor.razor *@
<CollabEditor TDoc="RichTextDocument"
              DocumentId="@($"page-{Id}")"
              Adapter="@adapter"
              UserDisplayName="@User?.Name">
    <Toolbar>
        @* Your custom toolbar *@
    </Toolbar>
    <Awareness>
        <PeerCursors />
        <PeerChat />
    </Awareness>
</CollabEditor>
```

Zero OT code in the application. Zero hub plumbing. Zero hand-rolled
presence. Your toolbar and business rules remain yours.

## 17. Roadmap

- **v0.1** — Text OT engine, in-memory storage, SignalR transport,
  Blazor adapter. Reproduces a typical hand-rolled collab editor
  with half the code.
- **v0.2** — Rich-text engine (Delta-style), adapters for
  `contenteditable` and ProseMirror.
- **v0.3** — Redis and EF Core storage, hybrid mode, Redis backplane.
  Health checks. JSON Schema generator.
- **v0.4** — Presence, awareness, chat, anchored comments.
- **v0.5** — Per-user undo/redo, labeled snapshots, diff.
- **v0.6** — Region-level permissions, validation hooks, audit sink.
- **v0.7** — Monaco, CodeMirror, TipTap adapters.
- **v0.8** — Offline support (Blazor and JS clients).
- **v0.9** — Branching/merging, suggestion mode.
- **v1.0** — Hardened, fuzz harness in CI, complete documentation,
  stability promise.

The roadmap is a strong intent, not a contract. Order may change
based on community input.

## 18. Non-goals

Equally important is what we will not do:

- **OpStream does not include a UI editor.** It synchronizes models;
  the editor is the host's. Adapters are thin, not editors.
- **OpStream is not a managed service.** It is a library, not SaaS.
- **OpStream does not reinvent identity.** It consumes
  `ClaimsPrincipal`.
- **OpStream does not force a particular store.** Memory is the
  default only because "three lines to start" matters; logs make it
  loud that it isn't for production.
- **OpStream does not render chat or comment UIs.** It provides data
  and events; the visual layer is the host's responsibility.
- **OpStream does not do formatting, OCR, or AI.** This is plumbing,
  not a product.

## 19. Open questions

These are honest questions where community input is welcome before
the relevant code is written:

- **Default schema format for codegen.** JSON Schema is the
  intermediate form, but should the on-disk representation be
  TypeBox, JSON Schema 2020-12, or NJsonSchema's flavor?
- **Backplane semantics on partition.** When `Backplane.Redis`
  reports a connection loss, do we freeze affected sessions until
  reconnection, or allow local edits with deferred broadcast?
- **First non-trivial CRDT engine to ship.** RGA for lists is on the
  roadmap, but real applications often want Yjs-compatible structures
  for direct interop. Worth shipping a Yjs-binary engine in v0.3?
- **Cursor rendering convention.** Should the adapter own cursor
  rendering (more flexibility) or should OpStream provide a default
  renderer that adapters override (less code per integration)?

Track these on GitHub Issues prefixed `[RFC]`.

## 20. License

OpStream is released under the **MIT License**. See
[`LICENSE`](LICENSE) for details.

## 21. Contributing

This is the first commit. The repository contains only this design
document; no code yet.

If you are interested in helping build OpStream:

- Read this document end-to-end, then open a GitHub Discussion with
  feedback on the architecture before code starts landing.
- Implementation will follow the roadmap order. The earliest help
  needed is on:
  1. The fuzz harness (so every subsequent engine PR has a quality
     bar to clear).
  2. The wire-protocol JSON Schema (so the JavaScript client can
     start in parallel).
- Code style, commit conventions, and PR templates will land
  alongside the first code PR.

Issues, RFCs, and discussions are welcome. Aggressive deadlines and
hidden agendas are not.

---

*This document is the design contract for OpStream. As the
implementation progresses, sections will be promoted to
specification status or moved out into dedicated documents. Anything
that changes here will be tracked in git so the architectural
history of the project remains visible.*
