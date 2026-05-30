# Storage overview

Storage is where OpStream keeps your documents so edits survive restarts and
people who join late (or reconnect) can catch up.

Start with the built-in **in-memory** store — zero setup, perfect for trying
OpStream and for tests. For anything real, point it at a database you already
run. You don't need a new one: pick whichever you're already using.

## Supported backends

| Backend | Best for | Setup |
|---|---|---|
| [In-memory](memory.md) | Tests, demos | Default |
| [EF Core](ef-core.md) | Existing EF Core code-first apps | `UseEfCoreStorage<TContext>()` |
| [SQL Server](sql-server.md) | Microsoft-centric stacks | `UseSqlServer(connStr)` |
| [PostgreSQL](postgresql.md) | OSS / cloud-native | `UsePostgreSqlStorage(...)` |
| [MySQL](mysql.md) | LAMP-style stacks | `UseMySqlStorage(...)` |
| [SQLite](sqlite.md) | Embedded / desktop / single-process | `UseSqliteStorage(...)` |
| [MongoDB](mongodb.md) | Document-first stacks | `UseMongoDbStorage(...)` |
| [Redis](redis.md) | High-throughput, RAM-resident | `UseRedisStorage(...)` |

`Use*` is **singleton-style** — calling it twice replaces the previous
registration. See [Builder API conventions](../reference/builder-api.md).

## Choosing a backend

- **You already use EF Core** → [EF Core](ef-core.md). Migrations land
  in your existing context.
- **You want maximum write throughput** → [Redis](redis.md). Sub-ms
  appends; persistence via AOF / RDB.
- **You want maximum simplicity for a single-server deploy** →
  [SQLite](sqlite.md). One file, no infrastructure.
- **You want operational maturity in production** →
  [PostgreSQL](postgresql.md) or [SQL Server](sql-server.md).
- **Your business data is in MongoDB** → [MongoDB](mongodb.md) keeps
  the op log next to it.

## What actually gets stored

Each backend keeps two things behind one `IDocumentStore` contract:

| What | Methods |
|---|---|
| The **op log** (append-only — every edit) | `AppendOpAsync` / `StreamOpsAsync` |
| **Snapshots** (compacted state, so you don't replay every op) | `SaveSnapshotAsync` / `LoadSnapshotAsync` |

Plus an optional **history store** (`IHistoryStore`) for the
[history / milestone subsystem](../operations/snapshots.md#history).

## Implementing a custom backend

The interface is small:

```csharp
public interface IDocumentStore
{
    Task AppendOpAsync(string documentId, StoredOp op, CancellationToken ct = default);
    IAsyncEnumerable<StoredOp> StreamOpsAsync(string documentId, long fromExclusive, CancellationToken ct = default);

    Task<DocumentSnapshot?> LoadSnapshotAsync(string documentId, CancellationToken ct = default);
    Task SaveSnapshotAsync(string documentId, DocumentSnapshot snapshot, CancellationToken ct = default);
}
```

Register your implementation with `services.AddSingleton<IDocumentStore, MyStore>()`
**after** `AddOpStream()`, or via a custom builder extension method.

## Default warnings

The default `MemoryDocumentStore` is registered as a fallback so
`AddOpStream()` works out of the box. The `DocumentRouter` logs a
**warning** at startup when this default is still active — that's your
signal to plug in a real backend before going live.
