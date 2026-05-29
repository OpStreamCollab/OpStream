# Document Lifecycle — Initialization & Finalization

This page describes the **complete lifecycle of a collaborative document** inside
OpStream: from the moment the first peer sends a `JoinDocument` request, through
collaborative editing, to the moment all peers leave and the host decides what
happens with the document's data.

!!! info "Scope"
    This document focuses on the **initialization** (join + session opening +
    seeding) and **finalization** (drain + host decision + cleanup) phases. For
    the step-by-step detail of the first join transport handshake, see
    [First User Join Flow](first-join-flow.md).

---

## High-level lifecycle overview

```mermaid
stateDiagram-v2
    direction LR

    [*] --> JoinRequested : Client calls JoinDocument()
    JoinRequested --> CheckStorage : DocumentRouter routes to owner

    state CheckStorage {
        direction TB
        LoadSnapshot --> SnapshotExists
        SnapshotExists --> RehydrateOps : Yes — restore state
        SnapshotExists --> CallSeeder : No — first time
    }

    state CallSeeder {
        direction TB
        HostSeeder --> SeederResult
        SeederResult --> HostReturnsState : Host returns initial state
        SeederResult --> EmptyDocument : Host seeder not configured
    }

    CheckStorage --> SessionActive : Session created & cached
    SessionActive --> Editing : Peer joins session
    Editing --> Editing : More peers join / ops applied
    Editing --> LastPeerLeaves : ActivePeersCount == 0
    LastPeerLeaves --> NotifyDrainHandlers : Notify host with final state

    state NotifyDrainHandlers {
        direction TB
        HostDecides --> KeepDecision : Keep
        HostDecides --> DeleteDecision : Delete
    }

    NotifyDrainHandlers --> IdleTimeout : Keep — schedule cleanup
    NotifyDrainHandlers --> PermanentDelete : Delete — remove all data

    IdleTimeout --> [*] : Session closed after 5 min
    PermanentDelete --> [*] : Data purged, cluster notified
```

---

## Phase 1 — Initialization (Join)

When a client calls `JoinDocument(documentId, documentType, protocolVersion)`,
the request travels through the transport layer (SignalR, WebSocket, or gRPC)
and reaches `DocumentRouter.JoinDocumentAsync`. The router orchestrates the
following decision tree to obtain or create the document session.

### 1.1 · Session resolution

```mermaid
flowchart TD
    A["JoinDocumentAsync()"]
    B{"Session already\nin memory?"}
    C["Return existing session"]
    D["OpenSessionAsync()"]
    E["IDocumentStore\n.LoadSnapshotAsync()"]
    F{"Snapshot\nexists?"}
    G["Deserialize snapshot\ncurrentRevision = snapshot.Revision"]
    H["No snapshot → revision = 0\nCall IDocumentSessionFactory.CreateSessionAsync()"]
    I["IDocumentSessionFactory\n.CreateSessionAsync()"]
    J{"snapshotData\nis present?"}
    K["Deserialize state\nfrom snapshot bytes"]
    L["Call IDocumentSeeder\n.GetInitialStateAsync()"]
    M{"Seeder returns\nnon-null?"}
    N["Use seeded state\nWrite initial snapshot to store"]
    O["❌ InvalidOperationException\nDocument cannot be initialized"]
    P["Stream & replay ops\nsince snapshot revision"]
    Q["Session cached in\n_activeSessions"]

    A --> B
    B -->|Yes| C
    B -->|No| D
    D --> E --> F
    F -->|Yes| G --> I
    F -->|No| H --> I
    I --> J
    J -->|Yes| K --> P
    J -->|No| L --> M
    M -->|Yes| N --> P
    M -->|No| O
    P --> Q
```

### 1.2 · The Document Seeder

The **seeder** is the extension point that allows the host application to
inject the initial state of a brand-new document — one that has never been
stored in OpStream's persistence layer.

```mermaid
sequenceDiagram
    participant Factory as TypedDocumentSessionFactory
    participant Store as IDocumentStore
    participant Seeder as IDocumentSeeder<TDoc>

    Factory->>Store: LoadSnapshotAsync(documentId)
    Store-->>Factory: null (no snapshot)

    Factory->>Seeder: GetInitialStateAsync(documentId)

    alt Host seeder configured
        Seeder-->>Factory: TDoc (initial state from host DB)
        Factory->>Store: WriteSnapshotAsync(documentId, state, revision=1)
        Note over Factory: Session starts at revision 1 with seeded state
    else Default EmptyDocumentSeeder
        Seeder-->>Factory: new TextDocument("") / new JsonDocument() / ...
        Factory->>Store: WriteSnapshotAsync(documentId, emptyState, revision=1)
        Note over Factory: Session starts at revision 1 with empty state
    else Seeder returns null
        Seeder-->>Factory: null
        Note over Factory: ❌ Throws InvalidOperationException
    end
```

#### Registering a custom seeder

```csharp
services.AddOpStream()
    .UseSeeder<TextDocument, MyTextSeeder>();
```

```csharp
public class MyTextSeeder : IDocumentSeeder<TextDocument>
{
    private readonly MyDbContext _db;

    public MyTextSeeder(MyDbContext db) => _db = db;

    public async ValueTask<TextDocument?> GetInitialStateAsync(
        string documentId, CancellationToken ct)
    {
        var entity = await _db.Documents
            .FindAsync(new object[] { documentId }, ct);

        if (entity is null)
            return null; // ← reject creation

        return new TextDocument(entity.Content);
    }
}
```

!!! tip "When the seeder returns `null`"
    Returning `null` from the seeder rejects the document creation entirely.
    The client will receive an error. Use this to prevent users from opening
    documents that don't exist in your domain.

#### Default behaviour — `EmptyDocumentSeeder`

If the host **does not** register a custom seeder, OpStream uses the built-in
`EmptyDocumentSeeder<TDoc>`. It creates a type-appropriate empty document:

| Document type | Empty state |
|---|---|
| `TextDocument` | `""` (empty string) |
| `RichTextDocument` | `{}` (empty rich-text document) |
| `Json_Document` | `{}` (empty JSON document) |
| `FormDocument` | `{}` (empty form) |
| `TableDocument` | `{}` (empty table) |
| `TreeDocument` | `{}` (empty tree) |

---

### 1.3 · Complete initialization sequence

The following diagram shows the full path from `JoinDocument` to the client
receiving the initial state, covering all three document-origin scenarios:
existing in storage, seeded by host, or created empty.

```mermaid
sequenceDiagram
    autonumber
    actor Client as 🌐 Client
    participant T as Transport
    participant R as DocumentRouter
    participant Store as IDocumentStore
    participant Factory as TypedDocumentSessionFactory
    participant Seeder as IDocumentSeeder<TDoc>
    participant DS as DocumentSession
    participant AS as AwarenessSession

    Client->>T: JoinDocument(docId, type, proto)
    T->>R: JoinDocumentAsync(docId, type, peerId, proto)

    Note over R: Protocol check + Authorization

    R->>R: GetSessionAsync(docId)

    alt Session already in memory
        R->>DS: JoinAsync(peerId)
    else Cold start
        R->>Store: LoadSnapshotAsync(docId)

        alt Snapshot found
            Store-->>R: DocumentSnapshot { Revision, State }
            R->>Factory: CreateSessionAsync(docId, revision, snapshotData)
            Factory->>Factory: Deserialize state from snapshot
        else No snapshot — call seeder
            Store-->>R: null
            R->>Factory: CreateSessionAsync(docId, 0, null)
            Factory->>Seeder: GetInitialStateAsync(docId)

            alt Host returns state
                Seeder-->>Factory: TDoc (pre-populated)
            else Empty default
                Seeder-->>Factory: TDoc (empty)
            end

            Factory->>Store: WriteSnapshotAsync(docId, initialState, rev=1)
        end

        R->>Store: StreamOpsAsync(docId, sinceRevision)
        loop Replay each op
            Store-->>R: StoredOp
            R->>DS: RehydrateOpAsync(op)
        end

        R->>R: Cache session in _activeSessions
        R->>DS: JoinAsync(peerId)
    end

    DS-->>R: DocumentStateResult { revision, snapshot, pendingOps }
    R->>AS: GetStates()
    AS-->>R: List<AwarenessState>

    R-->>T: SessionJoinResult { Revision, Snapshot, Awareness }
    T-->>Client: ✅ Document ready
```

---

## Phase 2 — Finalization (Drain)

When the **last peer** disconnects from a document, the document "drains".
OpStream notifies the host application with the final state and lets the host
decide what should happen next.

### 2.1 · Peer disconnect flow

```mermaid
flowchart TD
    A["Peer disconnects\n(transport close or explicit leave)"]
    B["DocumentRouter\n.RemovePeerFromAllSessionsAsync(peerId)"]
    C["DocumentSession.LeaveAsync(peerId)\nRemove from _activePeers"]
    D{"ActivePeersCount\n== 0?"}
    E["Other peers still\nconnected — done"]
    F["NotifyDrainHandlersAsync(session)\nCapture final state"]
    G{"Any IDocumentDrainHandler\nregistered?"}
    H["Default: Keep"]
    I["Run all handlers\nwith DocumentDrainContext"]
    J{"Any handler returns\nDelete?"}
    K["Decision = Keep"]
    L["Decision = Delete"]
    M["ScheduleSessionClosure(docId)\n5-minute idle timer"]
    N["DeleteDrainedDocumentAsync(docId)\nPermanent removal"]

    A --> B --> C --> D
    D -->|No| E
    D -->|Yes| F --> G
    G -->|No| H --> M
    G -->|Yes| I --> J
    J -->|No| K --> M
    J -->|Yes| L --> N
```

### 2.2 · The Drain Handler

The `IDocumentDrainHandler` is the host's opportunity to capture the final
document state and decide its fate.

```mermaid
sequenceDiagram
    participant R as DocumentRouter
    participant Scope as DI Scope
    participant H1 as IDocumentDrainHandler (1)
    participant H2 as IDocumentDrainHandler (2)
    participant Store as IDocumentStore
    participant History as IHistoryStore
    participant BP as IBackplane

    Note over R: Last peer left — build context

    R->>R: Capture DocumentDrainContext<br/>{ DocumentId, DocumentType,<br/>  Revision, State, DrainedAt }

    R->>Scope: CreateScope()
    R->>H1: OnDocumentDrainedAsync(ctx)
    H1-->>R: Keep ✅

    R->>H2: OnDocumentDrainedAsync(ctx)
    H2-->>R: Delete 🗑️

    Note over R: Delete wins (any handler can trigger it)

    alt Decision = Delete
        R->>R: CloseSessionAsync(docId)
        R->>Store: DeleteAsync(docId)
        R->>History: DeleteAsync(docId)
        R->>BP: PublishAsync("DocumentDeleted", docId)
        R->>R: ReleaseOwnershipAsync(docId)
        Note over R: All data permanently removed
    else Decision = Keep
        R->>R: ScheduleSessionClosure(docId)
        Note over R: Session closes after 5-min idle
    end
```

#### Registering a drain handler

```csharp
services.AddOpStream()
    .AddDocumentDrainHandler<PersistAndDeleteHandler>();
```

```csharp
public class PersistAndDeleteHandler : IDocumentDrainHandler
{
    private readonly MyDbContext _db;

    public PersistAndDeleteHandler(MyDbContext db) => _db = db;

    public async ValueTask<DocumentDrainDecision> OnDocumentDrainedAsync(
        DocumentDrainContext ctx, CancellationToken ct = default)
    {
        // Save the final state to the host's own database
        var entity = await _db.Documents.FindAsync(
            new object[] { ctx.DocumentId }, ct);

        if (entity is not null)
        {
            entity.Content = Encoding.UTF8.GetString(ctx.State.Span);
            entity.LastRevision = ctx.Revision;
            entity.UpdatedAt = ctx.DrainedAt;
            await _db.SaveChangesAsync(ct);
        }

        // Tell OpStream to delete the document data
        return DocumentDrainDecision.Delete;
    }
}
```

!!! warning "Delete is permanent"
    When a drain handler returns `DocumentDrainDecision.Delete`, OpStream
    removes **all** data associated with the document: current state, op log,
    snapshots, and history. This is irreversible. Make sure you have persisted
    the final state before returning `Delete`.

### 2.3 · The `DocumentDrainContext`

The context record passed to every drain handler contains:

| Field | Type | Description |
|---|---|---|
| `DocumentId` | `string` | The id of the document that drained |
| `DocumentType` | `string` | The engine type discriminator (e.g. `"text"`, `"json"`) |
| `Revision` | `long` | The final accepted revision |
| `State` | `ReadOnlyMemory<byte>` | The full, serialized document state (UTF-8 JSON) |
| `DrainedAt` | `DateTimeOffset` | UTC timestamp of the drain event |

### 2.4 · Keep path — idle timeout

When no drain handler requests deletion (or no handlers are registered at
all), the document follows the **Keep** path:

```mermaid
flowchart LR
    A["Decision = Keep"]
    B["ScheduleSessionClosure(docId)"]
    C["5-minute ITimer created"]
    D{"New peer joins\nbefore timeout?"}
    E["Timer cancelled\nSession stays warm"]
    F["CloseSessionAsync(docId)"]
    G["Session, awareness,\nsubscription, lock\nall disposed"]

    A --> B --> C --> D
    D -->|Yes| E
    D -->|No| F --> G
```

The document data **remains in storage** and can be reopened at any time by a
future `JoinDocument` call. The 5-minute idle timer prevents memory leaks when
documents go quiet.

### 2.5 · Delete path — permanent removal

When any drain handler returns `DocumentDrainDecision.Delete`:

```mermaid
flowchart TD
    A["Decision = Delete"]
    B["CloseSessionAsync(docId)\nDispose session + awareness + subscription"]
    C["IDocumentStore.DeleteAsync(docId)\nRemove state + op log + snapshots"]
    D["IHistoryStore.DeleteAsync(docId)\nRemove version history"]
    E["Backplane.PublishAsync\n'DocumentDeleted' to all nodes"]
    F["ReleaseOwnershipAsync(docId)\nFree cluster lock"]
    G["✅ Document fully purged"]

    A --> B --> C --> D --> E --> F --> G
```

The cluster-wide broadcast ensures that **every node** drops any cached state
for this document. If a client tries to join the document after deletion, a
fresh session will be created from scratch (going through the seeder again).

---

## Complete lifecycle diagram

```mermaid
flowchart TD
    subgraph INIT ["Phase 1 — Initialization"]
        direction TB
        J["Client: JoinDocument()"]
        AUTH["Authorization + Protocol check"]
        OWN["Ownership acquisition"]
        MEM{"Session in\nmemory?"}
        LOAD["LoadSnapshotAsync()"]
        SNAP{"Snapshot\nexists?"}
        SEED["IDocumentSeeder\n.GetInitialStateAsync()"]
        SEED_OK{"Seeder\nresult?"}
        DESER["Deserialize snapshot"]
        REPLAY["StreamOpsAsync()\nRehydrate pending ops"]
        CACHED["Session cached\nin _activeSessions"]
        JOINED["Peer joins session\nSnapshot + Awareness returned"]

        J --> AUTH --> OWN --> MEM
        MEM -->|Yes| JOINED
        MEM -->|No| LOAD --> SNAP
        SNAP -->|Yes| DESER --> REPLAY
        SNAP -->|No| SEED --> SEED_OK
        SEED_OK -->|"State returned"| REPLAY
        SEED_OK -->|"null"| ERR["❌ Document rejected"]
        REPLAY --> CACHED --> JOINED
    end

    subgraph EDIT ["Collaborative Editing"]
        direction TB
        EDITING["Peers apply ops\nAwareness updates\nBackplane fan-out"]
    end

    subgraph FINAL ["Phase 2 — Finalization"]
        direction TB
        LEAVE["Last peer disconnects"]
        DRAIN["NotifyDrainHandlersAsync()"]
        DECIDE{"Host decision?"}
        KEEP["Keep → 5-min idle timer\nData stays in storage"]
        DELETE["Delete → Purge all data\nCluster-wide eviction"]

        LEAVE --> DRAIN --> DECIDE
        DECIDE -->|Keep| KEEP
        DECIDE -->|Delete| DELETE
    end

    JOINED --> EDITING
    EDITING --> LEAVE
```

---

## Summary

| Phase | What happens | Key extension point |
|---|---|---|
| **Join — document exists in storage** | Snapshot loaded, ops replayed, session opened | — |
| **Join — new document, seeder configured** | `IDocumentSeeder<TDoc>.GetInitialStateAsync()` provides initial state | `UseSeeder<TDoc, TSeeder>()` |
| **Join — new document, no seeder** | `EmptyDocumentSeeder` creates a type-appropriate empty document | Default behaviour |
| **Last peer leaves — Keep** | Session scheduled for closure after 5-minute idle period | — |
| **Last peer leaves — Delete** | All document data permanently removed, cluster notified | `AddDocumentDrainHandler<THandler>()` |

---

## Related pages

<div class="grid cards" markdown>

- :material-login: **[First User Join Flow](first-join-flow.md)**
  Step-by-step detail of the transport handshake and session creation.

- :material-graph: **[Architecture overview](../architecture.md)**
  The layered model and how every component fits together.

- :material-database: **[Storage](../storage/index.md)**
  How snapshots and op logs are persisted.

- :material-scale-balance: **[Backplane](../operations/backplane.md)**
  How owner nodes are elected and how ops are fanned out in a cluster.

- :material-cog: **[Configuration (DI)](../reference/configuration.md)**
  Full reference for the builder API and all `Use*` / `Add*` methods.

</div>
