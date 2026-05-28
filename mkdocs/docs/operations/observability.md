# Observability

OpStream emits OpenTelemetry traces, metrics, and EventSource events
**by default**. You don't have to enable anything — pointing your
existing OpenTelemetry collector at the host process is enough.

## Activity source

All spans are emitted under the `OpStream` activity source
(`OpStreamTelemetry.ActivitySource`).

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("OpStream")        // OpStream's traces
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());
```

## Span hierarchy

A single op-apply produces a tree of spans rooted in the inbound
transport request:

```
opstream.session.apply_op
├─ opstream.engine.transform   (if rebase needed)
├─ opstream.engine.apply
├─ opstream.store.append
└─ opstream.backplane.publish
```

Tags on every span: `doc.id`, `peer.id`. The root adds `op.size`,
`op.base_revision`, `op.new_revision`, `engine.name`, `op.transforms`.

Errors are tagged `ActivityStatusCode.Error` with the reason
(`"Forbidden"`, `"Rejected by validator"`, exception message).

## Metrics

| Metric | Type | Description |
|---|---|---|
| `opstream.active_documents` | UpDownCounter | Sessions currently in memory. |
| `opstream.operations_processed` | Counter | Ops accepted. |
| `opstream.operations_rejected` | Counter | Ops rejected (auth, validator, rebase failure). |
| `opstream.apply_latency` | Histogram (ms) | End-to-end ApplyOpAsync latency. |
| `opstream.transform_count_per_op` | Histogram | Number of OT transforms per accepted op. |
| `opstream.store_append_latency` | Histogram (ms) | Storage append latency. |
| `opstream.store_read_latency` | Histogram (ms) | Storage stream-ops latency during rebase. |
| `opstream.backplane_publish_latency` | Histogram (ms) | Backplane fan-out latency. |
| `opstream.broadcast_fanout` | Histogram | Approximate peer count receiving each broadcast. |
| `opstream.peers_per_document` | Histogram | Active peers per session, sampled on apply. |

Register the meter explicitly if your OTel pipeline requires it:

```csharp
.WithMetrics(m => m
    .AddMeter("OpStream")
    .AddOtlpExporter());
```

## EventSource

For ETW / dotnet-trace / PerfView consumers, `OpStreamEventSource`
emits high-resolution events on the same boundaries (op applied, op
rejected, active-doc count adjustments). Useful for in-process
profiling without standing up an OTel collector.

## Health checks

`AddOpStream()` registers two health checks under tags `opstream`,
`storage` / `backplane`:

```csharp
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = h => h.Tags.Contains("opstream")
});
```

| Check | Default | Replaced when |
|---|---|---|
| `opstream-storage` | `MemoryStorageHealthCheck` (Degraded) | You call `UseSqlServer()` / `UseRedisStorage()` / etc. |
| `opstream-backplane` | `LocalBackplaneHealthCheck` (Healthy) | You call `UseRedisBackplane()`. |

Each storage / backplane provider package overrides the check with a
provider-specific ping.

## Diagnostics endpoint

For ops / SRE visibility, the Aspire integration ships a diagnostics
endpoint at `/opstream/diag/{docId}`:

```csharp
// Program.cs (when using OpStream.Hosting.Aspire)
app.MapOpStreamDiagnostics();
```

Returns a JSON document with:

- Active-on-this-node flag.
- Current owner node id.
- Current revision and peer list.
- Tail of the op log (configurable, default 50 entries).

Useful for "is anyone editing document X right now and where?" debugging.

## Logging

Standard `ILogger<T>` with structured scopes — every log line emitted
during `ApplyOpAsync` is automatically tagged with `doc.id` and
`peer.id` via a logging scope.

```
info: OpStream.Server.Session.DocumentSession[0]
      => doc.id=doc-42 peer.id=peer-7
      Op received (size=128B, baseRevision=14)
```

## See also

- [Deployment](deployment.md) — production checklists.
