# SQLite storage

Single-file SQLite backend. Perfect for desktop / embedded apps, single-server
deployments, and integration tests.

## Install

```bash
dotnet add package OpStream.Server.Storage.SQLite
```

## Setup

```csharp
builder.Services
    .AddOpStream()
    .UseSqliteStorage(options =>
    {
        options.ConnectionString = "Data Source=opstream.db";
        options.AutoMigrate      = true;
    });

// Or the shorthand
builder.Services.AddOpStream().UseSqlite("Data Source=opstream.db");
```

## Concurrency

OpStream serializes writes per-document via the session lock, so the
SQLite writer-lock contention you'd otherwise expect under concurrent
load is mostly avoided. Enable WAL for additional reader concurrency:

```
Data Source=opstream.db;Cache=Shared;Pooling=true;Mode=ReadWriteCreate;Foreign Keys=true;Journal Mode=WAL;
```

## When to pick this

- :material-check: Desktop / Electron / MAUI app that ships its own server.
- :material-check: Single-server SaaS where ops staying in one file is fine.
- :material-check: Integration tests — no infrastructure to spin up.
- :material-close: Multi-node deployments — SQLite is not designed for
  concurrent writers from multiple processes. Use a real RDBMS.
