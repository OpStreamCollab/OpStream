# Session

A `DocumentSession<TDoc, TOp>` is the **in-memory home of an open document** on the
server. Where [Storage](../storage/index.md) is the durable source of truth, the session
is the live, hot copy that clients actually interact with while they're collaborating.

## What it owns

| Responsibility | Detail |
|---|---|
| **Current state** | The deserialized `TDoc` value, kept up to date as ops are applied. |
| **Apply lock** | A per-document lock that serializes op application, so concurrent ops are rebased and committed one at a time — this is what makes convergence deterministic. |
| **Persistence** | Writes accepted ops to the op log and periodic [Snapshots](../operations/snapshots.md) through the store. |
| **Fan-out** | Broadcasts accepted ops and [Awareness](../engines/awareness.md) to peers via the [Backplane](../operations/backplane.md). |
| **Revision counter** | Holds the authoritative current [Revision](revision.md) used to rebase incoming ops. |

## Lifecycle

```mermaid
flowchart LR
    A[First peer joins] --> B[Load latest snapshot]
    B --> C[Replay ops after snapshot]
    C --> D[Session live: apply / broadcast / persist]
    D -->|idle timeout| E[Session closed]
    E -->|next join| A
```

- **Created lazily** by the [Document Router](document-router.md) when the first
  [peer](peer.md) joins a document that has no live session on this node.
- **Rehydrated** by loading the most recent snapshot and replaying only the ops applied
  after it — bounding startup cost no matter how long the op log is.
- **Drained** when the last peer disconnects: a final snapshot is taken and any registered
  drain handlers are notified (see below).
- **Closed** after an idle timeout (default **5 minutes**) with no connected peers. State
  is not lost — it's reconstructed from storage on the next join.

## Draining — the last peer leaves

When the **last** peer disconnects, the document *drains*. At that moment OpStream invokes
any registered `IDocumentDrainHandler`, handing it the **final, complete document state** —
its id, type, revision, and serialized bytes. This is the host's chance to persist the
finished document into its own database, push it to object storage, or kick off a downstream
workflow.

The handler can also return `DocumentDrainDecision.Delete` to tell OpStream to **delete the
document and all of its data** (current state, op log, snapshots, and history) right away —
typically once the host has safely captured the final version itself.

Register a handler through DI:

```csharp
services.AddOpStream()
    .AddDocumentDrainHandler<MyDrainHandler>();
```

```csharp
public sealed class MyDrainHandler(MyDbContext db) : IDocumentDrainHandler
{
    public async ValueTask<DocumentDrainDecision> OnDocumentDrainedAsync(
        DocumentDrainContext ctx, CancellationToken ct = default)
    {
        await db.SaveDocumentAsync(ctx.DocumentId, ctx.Revision, ctx.State, ct);
        // Hand-off complete — let OpStream drop its copy.
        return DocumentDrainDecision.Delete;
    }
}
```

See [Configuration → Document drain handler](../reference/configuration.md#document-drain-handler).

## One owner at a time

In a cluster, a document has a live session on **exactly one node** — its owner. If a peer
connects to a non-owner node, the router proxies the call to the owner so there is a single
authoritative apply lock and revision counter per document. See the
[ownership model](../operations/backplane.md#ownership-model).

Destructive management operations (delete, branch delete, etc.) **evict the live session
before touching storage**, so a stale in-memory copy can never re-persist deleted state.

## See also

- [Document Router](document-router.md) — creates and routes into sessions.
- [Snapshot](../operations/snapshots.md) — bounds session rehydration cost.
- [Revision](revision.md) — the counter the session arbitrates.
