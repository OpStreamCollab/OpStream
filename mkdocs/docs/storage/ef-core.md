# Entity Framework Core storage

Persists the op log and snapshots in any database EF Core supports,
through your existing `DbContext`.

## Install

```bash
dotnet add package OpStream.Server.Storage.EntityFrameworkCore
```

## Setup

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("App")));

builder.Services
    .AddOpStream()
    .UseEfCoreStorage<AppDbContext>();
```

The package adds two entities to your context — `OpStreamStoredOp` and
`OpStreamSnapshot` — via `IModelConfiguration`. Run `dotnet ef migrations
add ...` to generate the tables.

## Provider compatibility

`UseEfCoreStorage<TContext>` is **provider-agnostic** — it works with
any EF Core provider you've already configured on your context:

- SQL Server
- PostgreSQL (Npgsql)
- MySQL (Pomelo)
- SQLite
- Oracle
- Cosmos DB
- …

If you already use EF Core, this is almost always the easiest path. For
fresh deployments without an existing context, the dedicated provider
packages ([SQL Server](sql-server.md), [PostgreSQL](postgresql.md), …)
add the schema directly without needing your own `DbContext`.

## Schema

The migration adds two tables (default names; customizable via
`OnModelCreating` overrides on your context):

```sql
CREATE TABLE OpStreamStoredOps (
    DocumentId  NVARCHAR(255) NOT NULL,
    Revision    BIGINT        NOT NULL,
    AuthorId    NVARCHAR(255) NOT NULL,
    Timestamp   DATETIMEOFFSET NOT NULL,
    Payload     VARBINARY(MAX) NOT NULL,
    EngineType  NVARCHAR(255) NOT NULL,
    PRIMARY KEY (DocumentId, Revision)
);

CREATE TABLE OpStreamSnapshots (
    DocumentId  NVARCHAR(255) PRIMARY KEY,
    Revision    BIGINT        NOT NULL,
    Timestamp   DATETIMEOFFSET NOT NULL,
    State       VARBINARY(MAX) NOT NULL
);
```

## Performance notes

- The op-log primary key `(DocumentId, Revision)` is also its
  cluster-aligned access pattern: every read is a forward scan from a
  base revision.
- Snapshots compact the load path — see [Snapshots](../operations/snapshots.md).
- For very high write throughput documents, consider Redis storage and
  use EF Core only for long-term archive.

## When to pick this

- :material-check: You already have a `DbContext` and want one
  migration story.
- :material-check: You're targeting an EF-Core-supported database that
  doesn't have a dedicated OpStream provider.
- :material-close: You need sub-millisecond write latency — use
  [Redis](redis.md).
