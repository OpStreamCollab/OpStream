# PostgreSQL storage

Dedicated PostgreSQL backend. Same shape as the [SQL Server](sql-server.md)
provider, adapted to Postgres' types and quoting.

## Install

```bash
dotnet add package OpStream.Server.Storage.PostgreSQL
```

## Setup

```csharp
builder.Services
    .AddOpStream()
    .UsePostgreSqlStorage(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("OpStream")!;
        options.SchemaName       = "opstream";   // default
        options.AutoMigrate      = true;
    });
```

A shorthand `UsePostgreSql(connStr)` accepts a raw connection string when
defaults are fine.

## Schema

```sql
CREATE TABLE opstream.stored_ops (
    document_id  TEXT       NOT NULL,
    revision     BIGINT     NOT NULL,
    author_id    TEXT       NOT NULL,
    timestamp    TIMESTAMPTZ NOT NULL,
    payload      BYTEA      NOT NULL,
    engine_type  TEXT       NOT NULL,
    PRIMARY KEY (document_id, revision)
);

CREATE TABLE opstream.snapshots (
    document_id  TEXT       PRIMARY KEY,
    revision     BIGINT     NOT NULL,
    timestamp    TIMESTAMPTZ NOT NULL,
    state        BYTEA      NOT NULL
);
```

## Performance notes

- The primary key on `(document_id, revision)` is the only index needed
  for OpStream's read pattern. Postgres' B-tree handles the
  forward-scan-from-base-revision shape very efficiently.
- Enable `synchronous_commit = local` on the OpStream database if you
  trade durability for throughput.
- Consider partitioning `stored_ops` by `document_id` hash if you cross
  ~50M ops.

## When to pick this

- :material-check: OSS / cloud-native stacks (Supabase, RDS, Aurora).
- :material-check: You want SQL operational simplicity with strong
  durability defaults.
- :material-close: You already use EF Core with Npgsql — pick
  [EF Core storage](ef-core.md) instead.
