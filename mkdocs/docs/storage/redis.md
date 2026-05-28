# Redis storage

In-memory, replicated, blisteringly fast storage. Pairs with the
[Redis backplane](../operations/backplane.md) for an all-Redis OpStream
deployment.

## Install

```bash
dotnet add package OpStream.Server.Storage.Redis
```

## Setup

```csharp
builder.Services
    .AddOpStream()
    .UseRedisStorage(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("Redis")!;
        options.KeyPrefix        = "opstream:";   // default
    });

// Or
builder.Services.AddOpStream().UseRedis(connStr);
```

## Key layout

| Pattern | Type | Purpose |
|---|---|---|
| `opstream:ops:{docId}` | Stream | Append-only op log; entry ids are revision numbers. |
| `opstream:snap:{docId}` | String (binary) | Latest snapshot blob. |
| `opstream:snap:{docId}:meta` | Hash | `revision`, `timestamp`. |

Streams give us O(1) appends and O(N) range reads from a base revision,
which matches `IDocumentStore`'s access pattern exactly.

## Durability

Redis is in-memory by default. For production, enable **AOF**
(`appendonly yes`, `appendfsync everysec`) on the OpStream Redis
instance, or use a managed offering with built-in persistence
(Elasticache, MemoryDB, Upstash, Azure Cache for Redis Premium).

The snapshot mechanism also acts as a compaction strategy: with regular
snapshots, you can `XTRIM` old op-log entries past the snapshot point
without losing the ability to rehydrate.

## When to pick this

- :material-check: Latency-sensitive workloads (real-time collaboration
  is the textbook case).
- :material-check: You're already running Redis (and now you can use it
  as both backplane *and* storage).
- :material-close: You require ACID across multiple documents — use a
  relational backend.
- :material-close: You need cold archive — pair Redis with a tiered
  archive job to your RDBMS / object store.
