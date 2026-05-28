# MySQL storage

Dedicated MySQL / MariaDB backend.

## Install

```bash
dotnet add package OpStream.Server.Storage.MySQL
```

## Setup

```csharp
builder.Services
    .AddOpStream()
    .UseMySqlStorage(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("OpStream")!;
        options.AutoMigrate      = true;
    });

// Or the shorthand
builder.Services.AddOpStream().UseMySql(connStr);
```

## Schema

The standard `stored_ops` + `snapshots` shape (see
[SQL Server](sql-server.md#schema) / [PostgreSQL](postgresql.md#schema))
with `MEDIUMBLOB` / `LONGBLOB` payload columns appropriate to MySQL.

## Performance notes

- Default `innodb_buffer_pool_size` matters more for OpStream's
  scan-by-prefix reads than for OLTP. Size it to fit your hottest documents.
- Ensure `innodb_doublewrite = ON` for durability; OpStream's append-only
  write pattern doesn't amplify the doublewrite cost significantly.

## When to pick this

- :material-check: LAMP-style stack, existing MySQL operational expertise.
- :material-close: Greenfield deploy — consider [PostgreSQL](postgresql.md)
  for stronger feature parity with OpStream's roadmap.
