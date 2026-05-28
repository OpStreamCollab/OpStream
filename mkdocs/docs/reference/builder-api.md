# Builder API reference

The DI surface of OpStream. Lives in
`Microsoft.Extensions.DependencyInjection` so it lights up just by
`using` the namespace.

## Naming convention

| Prefix | Style | Behavior |
|---|---|---|
| `Use*()` | Singleton | Last call wins — replaces the previous registration. |
| `Add*()` | Collection | Each call adds one element to a multi-instance collection. |

## Entry point

### `AddOpStream`

```csharp
public static IOpStreamBuilder AddOpStream(
    this IServiceCollection services,
    Action<OpStreamOptions>? configure = null);
```

Registers the core services and sensible defaults:

- `TextOtEngine` registered for document type `"text"`.
- `MemoryDocumentStore` (warning at startup; **not for production**).
- `LocalBackplane` (single-node, in-process fan-out).
- `AllowAllAuthorizer` (warning at startup; **not for production**).
- `HybridSnapshotPolicy(100, 5 min)`.
- Health checks under tags `opstream`, `storage`, `backplane`.

Returns an `IOpStreamBuilder` you chain further configuration onto.

## Engine registration

### `AddEngine`

```csharp
public static IOpStreamBuilder AddEngine<TDoc, TOp, TEngine>(
    this IOpStreamBuilder builder,
    string documentType)
    where TEngine : class, IOpEngine<TDoc, TOp>;
```

Registers an engine for a document-type discriminator string. Call once
per document type your app handles. Multiple document types are
supported; the router looks up the right engine by the discriminator
each client sends with its `JoinDocument` call.

```csharp
services.AddOpStream()
    .AddEngine<TextDocument, TextOp, TextOtEngine>("text/markdown")
    .AddEngine<RichTextDocument, RichTextOp, RichTextEngine>("rich-text")
    .AddEngine<TreeDocument, TreeOpBatch, TreeCrdtEngine>("blocks");
```

### `AddValidator`

```csharp
public static IOpStreamBuilder AddValidator<TOp, TValidator>(this IOpStreamBuilder builder)
    where TValidator : class, IOpValidator<TOp>;
```

Registers a per-op validator. Multiple validators are evaluated in
registration order; the first one that returns `false` rejects the op.

### `UseSeeder`

```csharp
public static IOpStreamBuilder UseSeeder<TDoc, TSeeder>(this IOpStreamBuilder builder)
    where TSeeder : class, IDocumentSeeder<TDoc>;
```

Replaces the default `EmptyDocumentSeeder<TDoc>` used to populate new
documents on first access.

## Storage

| Method | Provider |
|---|---|
| `UseMemoryStorage()` | In-memory (default; for tests only) |
| `UseEfCoreStorage<TContext>()` | Entity Framework Core |
| `UseSqlServer(connStr)` / `UseSqlServerStorage(opts)` | SQL Server |
| `UsePostgreSql(connStr)` / `UsePostgreSqlStorage(opts)` | PostgreSQL |
| `UseMySql(connStr)` / `UseMySqlStorage(opts)` | MySQL / MariaDB |
| `UseSqlite(connStr)` / `UseSqliteStorage(opts)` | SQLite |
| `UseMongoDbStorage(opts)` | MongoDB |
| `UseRedis(connStr)` / `UseRedisStorage(opts)` | Redis |

See [Storage backends](../storage/index.md).

## Backplane

| Method | Behavior |
|---|---|
| `UseLocalBackplane()` | Single-node in-process fan-out (default). |
| `UseRedisBackplane(connStr or opts)` | Multi-node Redis backplane. |

See [Backplane](../operations/backplane.md).

## Authorization

### `UseAuthorization`

```csharp
public static IOpStreamBuilder UseAuthorization<TAuthorizer>(this IOpStreamBuilder builder)
    where TAuthorizer : class, IDocumentAuthorizer;
```

Replaces the default `AllowAllAuthorizer` with a scoped implementation
that runs once per request. See [Authorization](../operations/authorization.md).

## Snapshots

### `UseSnapshotPolicy`

```csharp
public static IOpStreamBuilder UseSnapshotPolicy(this IOpStreamBuilder builder, ISnapshotPolicy policy);
```

Replaces the default `HybridSnapshotPolicy(100, 5 min)`. See
[Snapshots](../operations/snapshots.md).

## Transports

| Method | Returns | Maps endpoint |
|---|---|---|
| `AddSignalRTransport()` | `IOpStreamBuilder` | `app.MapOpStreamSignalR(path?)` |
| `AddWebSocketTransport()` | `IOpStreamBuilder` | `app.MapOpStreamWebSockets(path?)` |
| `AddGrpcTransport()` | `IOpStreamBuilder` | `app.MapOpStreamGrpc()` |

You can register **more than one** transport in the same app.

## OpStreamConstants

The string constants used on the wire — exposed so clients and tests can
reference them instead of hard-coding strings.

| Class | Contents |
|---|---|
| `OpStreamConstants.HubMethods` | SignalR client→server method names |
| `OpStreamConstants.ClientEvents` | SignalR server→client event names |
| `OpStreamConstants.BackplaneCommands` | RPC types over the backplane |
| `OpStreamConstants.BackplaneMessages` | Fan-out message types |
| `OpStreamConstants.Engines` | Built-in engine name constants |

## FractionalIndex

`OpStream.Server.Engine.Common.FractionalIndex.Between(left, right)` —
strict-between key generator for siblings in Tree / Table CRDTs.

```csharp
public static string Between(string? left, string? right);
```

- Pass `null` on either side for an open boundary.
- Throws `ArgumentException` if `left >= right` or no alphabet-valid
  intermediate exists (e.g. `Between("a", "a!")`).

## Obsolete aliases

The following exist for backwards compatibility and will be removed in v1.0:

| Old | Use instead |
|---|---|
| `AddOpStreamServer()` | `AddOpStream()` |
| `ConfigureDocumentType<...>(type)` | `AddEngine<...>(type)` |
| `UseNoopBackplane()` | `UseLocalBackplane()` |
