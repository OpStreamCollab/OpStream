# SQL Server storage

Dedicated SQL Server backend for OpStream — managed schema, indexes, and
connection lifecycle. No `DbContext` required.

## Install

```bash
dotnet add package OpStream.Server.Storage.SqlServer
```

## Setup

```csharp
builder.Services
    .AddOpStream()
    .UseSqlServer(builder.Configuration.GetConnectionString("OpStream")!);
```

The package owns the connection string; on first startup it ensures the
op-log and snapshot tables exist. Migrations between OpStream versions
ship as idempotent SQL that runs on startup.

## Configuration via options

```csharp
services.AddOpStream()
    .UseSqlServerStorage(options =>
    {
        options.ConnectionString = "...";
        options.SchemaName       = "opstream";  // default: dbo
        options.AutoMigrate      = true;        // default: true
    });
```

Setting `AutoMigrate = false` is recommended when you ship schema changes
via your own pipeline; in that case ship the SQL from
`OpStream.Server.Storage.SqlServer/Scripts/` yourself.

## Schema

Same shape as the [EF Core](ef-core.md#schema) backend:

```sql
opstream.StoredOps   (DocumentId, Revision, AuthorId, Timestamp, Payload, EngineType)
opstream.Snapshots   (DocumentId, Revision, Timestamp, State)
```

Indexes:

- `PK_StoredOps (DocumentId, Revision)` — clustered, primary access pattern.
- `PK_Snapshots (DocumentId)` — clustered.

## Performance notes

- Use a dedicated database file group or pool to isolate from your
  business workload — OpStream's write pattern is append-only and
  scan-by-prefix; mixing it with OLTP workloads on the same indexes
  fragments both.
- Backups: `StoredOps` is append-only, so incremental backups are very
  cheap. `Snapshots` are upserted at most once per snapshot policy
  threshold.

## When to pick this

- :material-check: Microsoft-centric stack, no existing EF Core context.
- :material-check: You want operational maturity (replication, backups,
  AGs) without writing migration code.
- :material-close: You already have a `DbContext` — use [EF Core](ef-core.md).
