# Configuration reference (Dependency Injection)

Every knob in OpStream is wired through `Microsoft.Extensions.DependencyInjection`.
This page is the exhaustive catalogue of those options: **what each one does, its
default value, and when you need to change it.**

All server-side extension methods live in the `Microsoft.Extensions.DependencyInjection`
namespace, so they light up as soon as you reference the package — no extra `using`
is required.

## Two builders

OpStream exposes two independent builder chains:

| Builder | Created by | Lives in | Purpose |
|---|---|---|---|
| `IOpStreamBuilder` | `services.AddOpStream()` | server host | Engines, storage, backplane, authorization, transports |
| `IOpStreamClientBuilder` | `services.AddOpStreamClient()` | client app (Blazor, MAUI, console) | Which transport the client uses to reach a server |

### Method naming convention

| Prefix | Lifetime | Behavior when called twice |
|---|---|---|
| `Use*()` | Singleton | **Last call wins** — replaces the previous registration. |
| `Add*()` | Collection | **Accumulates** — each call registers one more element. |

---

## Server entry point — `AddOpStream`

```csharp
public static IOpStreamBuilder AddOpStream(
    this IServiceCollection services,
    Action<OpStreamOptions>? configure = null);
```

Registers the core services plus a set of **development-friendly defaults**. The
`configure` delegate is optional; pass it to tune `OpStreamOptions`.

```csharp
builder.Services.AddOpStream(options =>
{
    options.History.Enabled = true;
    options.History.SnapshotRevisionInterval = 200;
    options.AutomaticMigrationsEnabled = true;
});
```

### What it registers by default

| Subsystem | Default registration | Production-ready? | Replace with |
|---|---|---|---|
| Document store | `MemoryDocumentStore` | ❌ logs a startup warning | `UseRedisStorage()`, `UseEfCoreStorage()`, … |
| History store | `MemoryDocumentStore` | ❌ | same as above |
| Comment store | `MemoryCommentStore` | ❌ | `UseEfCoreCommentStorage()`, `UseRedisCommentStorage()`, … |
| Versioning ref store | `MemoryDocumentRefStore` | ❌ | `UseEfCoreVersioningStorage()` |
| Backplane | `LocalBackplane` (single-node) | ✅ for single node | `UseRedisBackplane()` for multi-node |
| Document authorizer | `AllowAllAuthorizer` | ❌ logs a startup warning | `UseAuthorization<T>()` |
| Management authorizer | `DenyAllDatabaseCommandAuthorizer` | ✅ fail-closed | `UseDatabaseCommandAuthorization<T>()` |
| Snapshot policy | `HybridSnapshotPolicy(100, 5 min)` | ✅ | `UseSnapshotPolicy()` |
| Default engine | `TextOtEngine` for type `"text"` | ✅ | `AddEngine<…>()` |
| Tenant provider | `DefaultTenantProvider` (single tenant) | ✅ | custom `ITenantProvider` |

!!! warning "The two authorizers behave oppositely by default"
    The **collaboration** path defaults to *allow-all* (so you can start coding
    immediately), while the **management** path defaults to *deny-all* (so you can
    never accidentally expose delete/purge to the world). Production deployments must
    replace **both**.

### `OpStreamOptions`

| Property | Type | Default | Function |
|---|---|---|---|
| `History` | `HistoryOptions` | new instance | Cold-store history configuration (see below). |
| `AutomaticMigrationsEnabled` | `bool` | `true` | When `true`, EF Core / SQL migrations are applied automatically at startup by the `MigrationHostedService`. Set `false` if you run migrations out-of-band (CI, DBA-managed). |

### `HistoryOptions` (`OpStreamOptions.History`)

| Property | Type | Default | Function |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Master switch for the cold-store history pipeline. When `false`, a `NoopHistorySnapshotter` is registered and no historical snapshots are written. Must be `true` for time-travel reads, version tags that survive compaction, and milestones. |
| `SnapshotInterval` | `TimeSpan?` | `null` | Write a history snapshot at least this often (wall-clock). `null` disables time-based history snapshots. |
| `SnapshotRevisionInterval` | `int?` | `null` | Write a history snapshot every *N* revisions. `null` disables revision-count-based history snapshots. |

!!! note
    `HistoryOptions` controls the **cold store** (durable, append-only history). It is
    distinct from `ISnapshotPolicy`, which controls the **hot store** snapshot cadence
    used to bound op-log replay. See [Snapshots](../operations/snapshots.md).

---

## Engines & document types

### `AddEngine<TDoc, TOp, TEngine>(documentType)`

```csharp
public static IOpStreamBuilder AddEngine<TDoc, TOp, TEngine>(
    this IOpStreamBuilder builder, string documentType)
    where TEngine : class, IOpEngine<TDoc, TOp>;
```

Registers an engine for a document-type discriminator string. Call once per document
type. The router selects the engine using the discriminator the client sends with its
`JoinDocument` call. `AddOpStream()` pre-registers `TextOtEngine` for `"text"`.

```csharp
services.AddOpStream()
    .AddEngine<RichTextDocument, RichTextOp, RichTextEngine>("rich-text")
    .AddEngine<TreeDocument, TreeOpBatch, TreeCrdtEngine>("blocks");
```

### `AddAnchorEngine<TOp>(documentType)`

Maps a document type to an already-registered `IAnchorEngine<TOp>` so that
`CompactWithAnchorsService` can rebase comment anchors during compaction. Call it
alongside `AddEngine` for every engine that supports anchored comments.
Defaults wired by `AddOpStream()`: `text`, `richtext`, `json`.

### `AddValidator<TOp, TValidator>()`

Registers a per-operation validator. Multiple validators run in registration order;
the first to return `false` rejects the op. **Collection-style** — add as many as you need.

### `UseSeeder<TDoc, TSeeder>()`

Replaces the default `EmptyDocumentSeeder<TDoc>` used to populate brand-new documents
on first access. Use it to give new documents non-empty initial content.

### `UseVersioningMerge<TDoc, TOp>(engineType)`

Registers a 3-way merge driver for an engine type so `VersioningRouter.MergeAsync` can
merge branches of that type. Without it, `MergeBranch` fails with *"No merge driver
registered"*. Call alongside `AddEngine` for every type that should support branch merges.

---

## Storage backends

All of these **replace** the default `MemoryDocumentStore` for both `IDocumentStore`
and `IHistoryStore`. Pick exactly one document store.

| Method | Provider | Required arguments |
|---|---|---|
| `UseMemoryStorage()` | In-memory (default; tests/dev only) | — |
| `UseEfCoreStorage<TContext>()` | Generic EF Core (any provider) | a configured `DbContext` |
| `UseSqlServerStorage(connStr)` | SQL Server | connection string |
| `UsePostgreSqlStorage(connStr)` | PostgreSQL (Npgsql) | connection string |
| `UseMySqlStorage(connStr)` | MySQL / MariaDB (Pomelo) | connection string |
| `UseSqliteStorage(connStr)` | SQLite | connection string |
| `UseMongoDbStorage(connStr, dbName)` | MongoDB | connection string + database name |
| `UseRedisStorage(connStr)` / `UseRedisStorage(opts)` | Redis | connection string |

The SQL providers ship pre-built migrations and register a `DbContextFactory`
automatically. See [Storage backends](../storage/index.md).

### `RedisStorageOptions`

Passed to the `UseRedisStorage(Action<RedisStorageOptions>)` overload.

| Property | Type | Default | Function |
|---|---|---|---|
| `ConnectionString` | `string` | `""` | **Required.** StackExchange.Redis connection string. Throws at startup if empty. |
| `SnapshotEvery` | `TimeSpan?` | `null` | How often a snapshot is written to Redis. When `null`, the global `ISnapshotPolicy` drives snapshots. |
| `OpLogRetention` | `TimeSpan?` | `null` | How long op-log entries are retained in Redis Streams. When `null`, entries are never auto-pruned. |

```csharp
services.AddOpStream()
    .UseRedisStorage(o =>
    {
        o.ConnectionString = "localhost:6379";
        o.SnapshotEvery    = TimeSpan.FromMinutes(2);
        o.OpLogRetention   = TimeSpan.FromDays(7);
    });
```

### Comment & versioning stores

These are **separate** registrations from the document store. Call them *after* the
matching document-store method so the shared connection/context already exists.

| Method | Replaces | Prerequisite |
|---|---|---|
| `UseEfCoreCommentStorage<TContext>()` | `ICommentStore` | `UseEfCoreStorage<TContext>()` |
| `UseMongoDbCommentStorage()` | `ICommentStore` | `UseMongoDbStorage()` |
| `UseRedisCommentStorage()` | `ICommentStore` | `UseRedisStorage()` |
| `UseEfCoreVersioningStorage<TContext>()` | `IDocumentRefStore` | `UseEfCoreStorage<TContext>()` |

---

## Backplane (scaling out)

| Method | Behavior | Default? |
|---|---|---|
| `UseLocalBackplane()` | Single-node, in-process fan-out. Peers on the same instance see each other. | ✅ |
| `UseRedisBackplane(connStr)` / `UseRedisBackplane(opts)` | Multi-node fan-out + distributed document ownership via Redis. | — |

### `RedisBackplaneOptions`

| Property | Type | Default | Function |
|---|---|---|---|
| `ConnectionString` | `string` | `""` | **Required.** Redis connection string for the backplane and ownership manager. Throws at startup if empty. |

```csharp
services.AddOpStream()
    .UseRedisStorage("localhost:6379")
    .UseRedisBackplane("localhost:6379");
```

Switching to the Redis backplane also swaps the `opstream-backplane` health check for a
Redis-aware probe. See [Backplane](../operations/backplane.md).

---

## Authorization

### `UseAuthorization<TAuthorizer>()`

Replaces the default `AllowAllAuthorizer` with a **scoped** `IDocumentAuthorizer` that
runs once per request, integrating with your host's identity/permission model. Governs
the **collaboration** path (join / op / awareness). See [Authorization](../operations/authorization.md).

### `UseDatabaseCommandAuthorization<TAuthorizer>()`

Replaces the default `DenyAllDatabaseCommandAuthorizer` with a **scoped**
`IDatabaseCommandAuthorizer`. One authorizer governs **both** the management plane
(list / inspect / delete / compact / purge) **and** the versioning plane (names /
branches / versions / merge). The host switches on `DatabaseCommandContext.Command`.

```csharp
services.AddOpStream()
    .UseAuthorization<MyDocumentAuthorizer>()
    .UseDatabaseCommandAuthorization<MyManagementAuthorizer>();
```

!!! danger "Management is deny-all until you wire this"
    Every management and versioning endpoint returns *Forbidden* until you register a
    real `IDatabaseCommandAuthorizer`. This is intentional fail-closed behavior.

---

## Snapshots

### `UseSnapshotPolicy(ISnapshotPolicy policy)`

Replaces the default `HybridSnapshotPolicy(100, TimeSpan.FromMinutes(5))` — which
snapshots the hot store after **100 ops or 5 minutes**, whichever comes first.

```csharp
services.AddOpStream()
    .UseSnapshotPolicy(new HybridSnapshotPolicy(maxOps: 250, maxInterval: TimeSpan.FromMinutes(2)));
```

See [Snapshots](../operations/snapshots.md).

---

## Document drain handler

### `AddDocumentDrainHandler<THandler>()`

Registers an `IDocumentDrainHandler` invoked when a document loses its **last peer** — i.e.
everyone editing it has disconnected and the document goes quiet ("drains"). The handler
receives the **final, complete document state** so the host can persist it into its own
database, archive it, or trigger a downstream workflow.

```csharp
public static IOpStreamBuilder AddDocumentDrainHandler<THandler>(this IOpStreamBuilder builder)
    where THandler : class, IDocumentDrainHandler;
```

The handler returns a `DocumentDrainDecision`:

| Decision | Effect |
|---|---|
| `Keep` (default) | Leave the document in place; the session closes after the normal idle grace period. |
| `Delete` | Permanently delete the document and **all** of its data — current state, op log, snapshots, and history — and broadcast a cluster-wide eviction. |

The `DocumentDrainContext` handed to the handler carries:

| Field | Type | Description |
|---|---|---|
| `DocumentId` | `string` | The drained document's id. |
| `DocumentType` | `string` | The type discriminator it was opened with (e.g. `"text"`). |
| `Revision` | `long` | The final accepted revision. |
| `State` | `ReadOnlyMemory<byte>` | The full current state as UTF-8 JSON (`OpStreamJsonOptions.Default`). |
| `DrainedAt` | `DateTimeOffset` | When it drained (UTC). |

Behavior notes:

- **Resolved per drain in a fresh scope**, so handlers may depend on **scoped** services
  such as a `DbContext`.
- **Multiple handlers** run in registration order. If **any** returns `Delete`, the document
  is deleted.
- An exception in one handler is logged and never blocks the others or the disconnect path.
- Fires each time the active-peer count transitions to zero. It does **not** fire on
  administrative delete/purge.

```csharp
services.AddOpStream()
    .AddDocumentDrainHandler<PersistOnDrainHandler>();

public sealed class PersistOnDrainHandler(MyDbContext db) : IDocumentDrainHandler
{
    public async ValueTask<DocumentDrainDecision> OnDocumentDrainedAsync(
        DocumentDrainContext ctx, CancellationToken ct = default)
    {
        await db.UpsertDocumentAsync(ctx.DocumentId, ctx.Revision, ctx.State.ToArray(), ct);
        return DocumentDrainDecision.Delete; // hand-off done — OpStream may drop its copy
    }
}
```

See [Session → Draining](../concepts/session.md#draining-the-last-peer-leaves).

---

## Awareness tunables — `AwarenessOptions`

Presence/cursor behavior. Currently consumed internally by `AwarenessEngine` /
`AwarenessSession` with the defaults below (constructed directly, not registered through a
dedicated `Use*` method).

| Property | Type | Default | Function |
|---|---|---|---|
| `Ttl` | `TimeSpan` | `30 s` | Inactivity window after which a peer's presence state is considered stale and evicted from snapshot reads. |
| `CoalesceIdenticalUpdates` | `bool` | `true` | When `true`, updates byte-equal to the peer's current stored state are suppressed (not broadcast, not re-stored). |

---

## Server transports

Register one or more transports, then map each to an endpoint. **You can run all three
simultaneously in the same app.**

| Register | Maps collaboration | Default pattern |
|---|---|---|
| `AddSignalRTransport()` | `MapOpStreamSignalR(pattern?)` | `/collab` |
| `AddWebSocketTransport()` | `MapOpStreamWebSockets(pattern?)` | `/collab-ws` |
| `AddGrpcTransport()` | `MapOpStreamGrpc()` | service route |

### Management & comments endpoints

Each transport also exposes optional management, versioning, and comments endpoints.
Registration is automatic with the transport; you only choose whether to **map** them.

| Endpoint mapping | Default pattern | Plane |
|---|---|---|
| `MapOpStreamSignalRManagement(pattern?)` | `/manage` | Documents / history |
| `MapOpStreamWebSocketsManagement(pattern?)` | `/manage-ws` | Documents / history |
| `MapOpStreamGrpcManagement()` | service route | Documents / history |
| `MapOpStreamWebSocketsComments(pattern?)` | `/comments-ws` | Comments |
| `MapOpStreamGrpcComments()` | service route | Comments |

```csharp
var app = builder.Build();

app.MapOpStreamSignalR();                 // /collab
app.MapOpStreamSignalRManagement();       // /manage
app.MapOpStreamWebSockets();              // /collab-ws
app.MapOpStreamGrpc();
```

See the [Transports overview](../transports/index.md).

---

## Client configuration

Start with `services.AddOpStreamClient()`, then chain a transport. The collaboration
client (`IOpStreamClient`) and the management client (`IOpStreamManagementClient`) are
configured independently — register either, both, or neither.

### Collaboration transports

| Method | Registers `IOpStreamClient` | Options type |
|---|---|---|
| `UseSignalRTransport(configure)` | `SignalROpStreamClient` | `OpStreamSignalROptions` |
| `UseWebSocketTransport(configure)` | `WebSocketOpStreamClient` | `OpStreamWebSocketOptions` |
| `UsegRPCTransport(configure)` | `gRPCOpStreamClient` | `OpStreamgRPCOptions` |

### Management transports

| Method | Registers `IOpStreamManagementClient` | Options type |
|---|---|---|
| `UseSignalRManagementTransport(configure)` | `SignalROpStreamManagementClient` | `OpStreamSignalRManagementOptions` |
| `UseWebSocketManagementTransport(configure)` | `WebSocketOpStreamManagementClient` | `OpStreamWebSocketManagementOptions` |
| `UsegRPCManagementTransport(configure)` | `gRPCOpStreamManagementClient` | `OpStreamgRPCManagementOptions` |

### Client options reference

| Options type | Property | Default | Function |
|---|---|---|---|
| `OpStreamSignalROptions` | `HubUrl` | `/collab` | Collaboration hub URL. |
| `OpStreamWebSocketOptions` | `ServerUri` | `ws://localhost:5000/ws-collab` | Collaboration WebSocket URI. |
| `OpStreamgRPCOptions` | `ServerAddress` | `https://localhost:5001` | gRPC server address. |
| `OpStreamSignalRManagementOptions` | `ManagementHubUrl` | `/mgmt` | Management hub URL. |
| | `VersioningHubUrl` | `/versioning` | Versioning hub URL. |
| `OpStreamWebSocketManagementOptions` | `ManagementWsUri` | `ws://localhost:5000/ws-mgmt` | Management WebSocket URI. |
| | `VersioningWsUri` | `ws://localhost:5000/ws-versioning` | Versioning WebSocket URI. |
| `OpStreamgRPCManagementOptions` | `Address` | `https://localhost:5001` | gRPC server address (shared by the management and versioning services). |

```csharp
// Collaboration client over SignalR
services.AddOpStreamClient()
    .UseSignalRTransport(o => o.HubUrl = "https://my-host/collab");

// Management client over gRPC
services.AddOpStreamClient()
    .UsegRPCManagementTransport(o => o.Address = "https://my-host:5001");
```

!!! note "Error mapping"
    Management clients translate server failures into `OpStreamManagementException`.
    Inspect `IsForbidden` to distinguish an authorization denial (server returned a
    `"Forbidden:"` message) from any other error.

---

## End-to-end example

A production multi-node host using PostgreSQL storage, a Redis backplane, real
authorization on both planes, and all three transports:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpStream(options =>
    {
        options.History.Enabled = true;
        options.History.SnapshotRevisionInterval = 200;
    })
    // Engines
    .AddEngine<RichTextDocument, RichTextOp, RichTextEngine>("rich-text")
    .AddAnchorEngine<RichTextOp>("rich-text")
    .UseVersioningMerge<RichTextDocument, RichTextOp>("rich-text")
    // Storage
    .UsePostgreSqlStorage(builder.Configuration.GetConnectionString("Db")!)
    .UseEfCoreCommentStorage<PostgreSqlOpStreamDbContext>()
    .UseEfCoreVersioningStorage<PostgreSqlOpStreamDbContext>()
    // Scale-out
    .UseRedisBackplane(builder.Configuration.GetConnectionString("Redis")!)
    // Security (both planes)
    .UseAuthorization<MyDocumentAuthorizer>()
    .UseDatabaseCommandAuthorization<MyManagementAuthorizer>()
    // Snapshot cadence
    .UseSnapshotPolicy(new HybridSnapshotPolicy(250, TimeSpan.FromMinutes(2)))
    // Transports
    .AddSignalRTransport()
    .AddWebSocketTransport()
    .AddGrpcTransport();

var app = builder.Build();

app.MapOpStreamSignalR();
app.MapOpStreamSignalRManagement();
app.MapOpStreamWebSockets();
app.MapOpStreamWebSocketsManagement();
app.MapOpStreamGrpc();
app.MapOpStreamGrpcManagement();

app.Run();
```

---

## Obsolete aliases

Kept for backwards compatibility; removed in v1.0.

| Old | Use instead |
|---|---|
| `AddOpStreamServer()` | `AddOpStream()` |
| `ConfigureDocumentType<…>(type)` | `AddEngine<…>(type)` |
| `UseNoopBackplane()` | `UseLocalBackplane()` |

See also the condensed [Builder API reference](builder-api.md).
