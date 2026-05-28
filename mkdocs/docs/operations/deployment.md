# Deployment checklist

A practical guide to getting an OpStream-powered app into production.

## Pre-flight

- [ ] **Storage replaced.** No `MemoryDocumentStore` in production —
      `UseSqlServer()` / `UseRedisStorage()` / `UseEfCoreStorage<T>()` etc.
- [ ] **Authorizer replaced.** No `AllowAllAuthorizer` in production —
      `UseAuthorization<MyAuthorizer>()` wired against your identity model.
- [ ] **Backplane chosen.** Single node → `LocalBackplane` is fine.
      Multi-node → `UseRedisBackplane()`.
- [ ] **Snapshot policy tuned.** Default 100 ops / 5 min is safe; tune
      for your document size and write rate.
- [ ] **CORS / Auth configured.** Standard ASP.NET Core checklist — OpStream
      doesn't add anything custom here.

## Single-node deployment

A single ASP.NET Core process running OpStream + your app:

```csharp
builder.Services
    .AddOpStream()
    .UseSqlServer(connStr)
    .UseAuthorization<MyAuthorizer>()
    .AddSignalRTransport();

builder.Services.AddSignalR();
app.MapOpStreamSignalR();
```

Storage durability is your only worry — back up the database normally.
No backplane needed.

## Multi-node deployment

Two or more nodes behind a load balancer + Redis:

```csharp
builder.Services
    .AddOpStream()
    .UseSqlServer(connStr)
    .UseRedisBackplane(redisConnStr)
    .UseAuthorization<MyAuthorizer>()
    .AddSignalRTransport();
```

### Load balancer

- **Sticky sessions are NOT required.** OpStream's router transparently
  proxies requests to the document's owner node via the backplane.
- A peer that lands on the "wrong" node still gets correct ordering and
  fan-out; the proxy adds one Redis round-trip per request.
- If you can enable sticky-by-cookie cheaply, it's a small latency win
  (avoids the proxy). Not required.

### Health checks

```csharp
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false      // process up?
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = h => h.Tags.Contains("opstream")
});
```

Use `/health/live` for the load balancer's liveness probe and
`/health/ready` for readiness — readiness goes false when storage or
the backplane can't be reached.

## Aspire deployment

If you're using **.NET Aspire** to orchestrate your topology:

```csharp
// AppHost
var redis = builder.AddRedis("redis");
var sql   = builder.AddSqlServer("sql").AddDatabase("opstream");

builder.AddProject<Projects.MyApp>("api")
    .WithReference(redis)
    .WithReference(sql);
```

Then in `MyApp/Program.cs`:

```csharp
builder.AddRedisClient("redis");
builder.AddSqlServerClient("opstream");

builder.Services
    .AddOpStream()
    .UseSqlServer(builder.Configuration.GetConnectionString("opstream")!)
    .UseRedisBackplane(builder.Configuration.GetConnectionString("redis")!);
```

See `OpStream.Hosting.Aspire` for the matching AppHost-side resource helpers.

## Logging and tracing

See [Observability](observability.md). At minimum:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("my-app"))
    .WithTracing(t => t.AddSource("OpStream").AddOtlpExporter())
    .WithMetrics(m => m.AddMeter("OpStream").AddOtlpExporter());
```

## Backup strategy

- **Op log**: append-only; incremental backups are cheap. Frequency
  matches your RPO.
- **Snapshots**: upserted; ship with the rest of the database.
- **Awareness**: ephemeral; never backed up.

## Common pitfalls

| Symptom | Likely cause | Fix |
|---|---|---|
| Edits don't reach the other client | Two server processes without backplane | `UseRedisBackplane()` |
| 403 on every op | Default `AllowAllAuthorizer` replaced incorrectly | Check DI registration order |
| Slow document load | Op log grew large without snapshots | Tune `UseSnapshotPolicy` |
| `MemoryStorage` warning in prod logs | Storage `Use*` call missing | Add the storage provider |
| Sticky-session client gets stale snapshot after failover | Ownership lease still held by failed node | Lower `OwnershipLeaseTtl` |
