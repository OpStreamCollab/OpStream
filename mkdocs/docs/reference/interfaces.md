# Engine contracts

The interfaces every engine plugs into. Reference for advanced users
writing custom engines or interoperating at a low level.

## `IOpEngine<TDoc, TOp>`

```csharp
public interface IOpEngine<TDoc, TOp>
{
    TDoc Apply(TDoc state, TOp op);
    TOp? Transform(TOp incoming, TOp existing, TransformPriority priority);
    TOp? Compose(TOp a, TOp b);
    TOp Invert(TOp op, TDoc preState);
    bool IsNoOp(TOp op);
    TOp RestampToWin(TOp op, TDoc currentState) => op;   // interface default
}
```

| Method | Required | Purpose |
|---|---|---|
| `Apply` | yes | Produce a new state from the current state + op. **Pure, no I/O.** |
| `Transform` | yes | Rebase an incoming op against a concurrent existing op. Return null if it fully absorbs. Identity for CRDT engines. |
| `Compose` | optional | Merge two sequential ops into one (return null if not supported). Used for snapshot compaction. |
| `Invert` | yes | Produce the op that undoes `op` against the pre-state. Required by `UndoRedoEngine`. |
| `IsNoOp` | yes | Detect ops with no observable effect. |
| `RestampToWin` | optional | LWW engines override to guarantee a cached inverse beats concurrent writers. OT / move-log engines inherit the identity default. |

### Purity

Engines must be **pure**: no I/O, no clock reads, no static state.
Timestamps are passed in via op payloads; randomness is forbidden.
The fuzz test harness depends on this — random op sequences are
generated and replayed across N replicas; any non-determinism fails the
build.

## `TransformPriority`

```csharp
public enum TransformPriority
{
    IncomingWins,
    ExistingWins,
}
```

Breaks ties when two ops would otherwise be equivalent (e.g. two
concurrent inserts at the same position). The server uses
`ExistingWins` so the op that landed first holds its ground.

## `IDocumentStore`

```csharp
public interface IDocumentStore
{
    Task AppendOpAsync(string documentId, StoredOp op, CancellationToken ct = default);
    IAsyncEnumerable<StoredOp> StreamOpsAsync(string documentId, long fromExclusive, CancellationToken ct = default);
    Task<DocumentSnapshot?> LoadSnapshotAsync(string documentId, CancellationToken ct = default);
    Task SaveSnapshotAsync(string documentId, DocumentSnapshot snapshot, CancellationToken ct = default);
}
```

Implement this to add a custom storage backend. See
[Storage overview](../storage/index.md#implementing-a-custom-backend).

## `IBackplane`

```csharp
public interface IBackplane
{
    string NodeId { get; }
    Task<IAsyncDisposable> SubscribeAsync(string documentId, Func<BackplaneMessage, ValueTask> handler, CancellationToken ct = default);
    Task PublishAsync(string documentId, BackplaneMessage message, CancellationToken ct = default);
    Task<BackplaneResponse> SendRequestAsync(string targetNodeId, string type, ReadOnlyMemory<byte> payload, CancellationToken ct = default);
    Task<IAsyncDisposable> RegisterRequestHandlerAsync(Func<BackplaneRequest, Task<BackplaneResponse>> handler, CancellationToken ct = default);
}
```

Pub/sub + RPC. Implement to plug in a non-Redis transport — see
[Backplane: Custom backplanes](../operations/backplane.md#custom-backplanes).

## `IDocumentAuthorizer`

```csharp
public interface IDocumentAuthorizer
{
    ValueTask<DocumentAccess> AuthorizeAsync(string documentId, CancellationToken ct = default);
}

public sealed record DocumentAccess(bool CanRead, bool CanWrite);
```

Scoped per request. See [Authorization](../operations/authorization.md).

## `IOpValidator<TOp>`

```csharp
public interface IOpValidator<TOp>
{
    ValueTask<bool> ValidateAsync(OpValidationContext<TOp> ctx, CancellationToken ct);
}

public record OpValidationContext<TOp>(string DocumentId, TOp Op);
```

Pre-apply validation. Register via
[`AddValidator`](builder-api.md#addvalidator).

## `IDocumentSeeder<TDoc>`

```csharp
public interface IDocumentSeeder<TDoc>
{
    Task<TDoc> SeedAsync(string documentId, CancellationToken ct = default);
}
```

Produces the initial state for a never-seen-before document. The
default `EmptyDocumentSeeder<TDoc>` returns `new TDoc()`. Override via
[`UseSeeder`](builder-api.md#useseeder) to hydrate from a template or
external source.

## `ITenantProvider` / `IDocumentIdGlobalizer`

See [Multi-tenancy](../operations/multitenancy.md).

## Ephemeral primitives

For building new ephemeral engines (cursors, hover overlays, …):

| Interface | Default | Purpose |
|---|---|---|
| `IEphemeralEngine<TState>` | `AwarenessEngine` | Pure Merge / IsExpired / IsNoOp policy. |
| `IPeerStateStore<TState>` | `PeerStateStore<TState>` | Concurrent in-memory per-peer store. |
| `IEphemeralChannel<TState>` | `BackplaneEphemeralChannel<TState>` | Cluster fan-out. |
