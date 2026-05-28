# Architecture

OpStream is built around one idea: **every layer is replaceable**. The transport
your clients speak, the storage that persists ops, the algorithm that merges
concurrent edits, the backplane that fans out across nodes — all of them sit
behind small interfaces you wire together with the standard ASP.NET Core DI
builder.

The result is a system that scales from a single Docker container serving two
browser tabs to a horizontally-scaled cluster fronting heterogeneous clients —
**without rewriting your application code**. You change configuration, not
architecture.

!!! tip "Three properties to keep in mind"
    - **Dynamic** — add transports, swap storage, enable the backplane at runtime via env vars or builder calls.
    - **Flexible** — mix engines, transports, and storage backends in any combination the matrix allows.
    - **Adaptable** — the server is .NET 9, but the wire protocol is plain JSON/Protobuf. **Clients can be written in anything.**

---

## The layered model

Every OpStream deployment, no matter how small or large, is the same five layers:

```mermaid
flowchart TB
    subgraph L1["1 · Clients · any technology"]
        direction LR
        L1A["🌐 Browser"]
        L1B["📱 Mobile"]
        L1C["🖥️ Desktop"]
        L1D["🤖 Bot / Agent"]
    end

    subgraph L2["2 · Transport layer · pluggable"]
        direction LR
        L2A["SignalR"]
        L2B["WebSocket"]
        L2C["gRPC"]
    end

    subgraph L3["3 · Core · routing · auth · sessions"]
        direction LR
        L3A["DocumentRouter"]
        L3B["DocumentSession"]
        L3C["Authorizer"]
    end

    subgraph L4["4 · Engines · OT / CRDT"]
        direction LR
        L4A["Text"]
        L4B["JSON"]
        L4C["Tree"]
        L4D["Table"]
        L4E["Form"]
        L4F["Rich text"]
    end

    subgraph L5["5 · Persistence + scale · swappable"]
        direction LR
        L5A[("Storage
        SQL · Mongo · Redis · …")]
        L5B[("Backplane
        local · Redis")]
    end

    L1 --> L2 --> L3 --> L4
    L3 --> L5
```

Each layer talks to the next through a small interface — `IDocumentTransport`,
`IOpEngine<TDoc, TOp>`, `IDocumentStore`, `IBackplane`. Replace one layer and
the others don't notice.

---

## Simplest deployment — one server, plain WebSockets

The minimum viable topology: a single `.NET` server with the in-memory store
and the local backplane, and two browsers connecting via raw WebSockets. No
infrastructure, no SignalR client library, **no .NET on the client** — just
HTML and JavaScript talking to a TCP socket.

```mermaid
graph TD
    S["⚙️ OpStream Server"]

    S <-->|WebSocket| CA
    S <-->|WebSocket| CB

    CA["🌐 HTML Client
    ✏️ Collaborative Editing
    📄 doc-42"]
    CB["🌐 HTML Client
    ✏️ Collaborative Editing
    📄 doc-42"]
```

This is exactly what you get from:

```bash
docker run --rm -p 8080:8080 opstreamcollab/opstream:latest
```

Perfect for prototyping, demos, and single-tenant edge boxes. **Zero
configuration.** The same image, with three environment variables changed,
becomes the cluster below.

---

## Multi-transport from a single process

OpStream's transports are independent — a single server process can listen on
**all three at once** on the same port. Kestrel is configured for
`Http1AndHttp2` everywhere, so SignalR (HTTP/1.1), WebSockets (HTTP/1.1), and
gRPC (HTTP/2) share the same TCP listener.

```mermaid
flowchart LR
    subgraph SERVER["🖥️ Single OpStream server · port 8080"]
        direction TB
        K["Kestrel
        HTTP/1.1 + HTTP/2"]
        SR["SignalR hub
        /collab"]
        WS["WebSocket endpoint
        /collab-ws"]
        GR["gRPC service
        /opstream.Collab"]
        K --> SR
        K --> WS
        K --> GR
    end

    A["⚡ Angular client"] -->|SignalR| SR
    B["🌐 HTML client"] -->|WebSocket| WS
    C["🔷 .NET Core client"] -->|gRPC| GR
    D["⚛️ React client"] -->|SignalR| SR

    SR --> R["DocumentRouter"]
    WS --> R
    GR --> R
    R --> DOC[("📄 doc-42")]
```

The router is transport-agnostic — by the time an op reaches it, it has been
normalized to the same wire model regardless of how it arrived. **A React app
on SignalR and a console tool on gRPC can collaborate on the same document
through the same process.**

Enable transports with a single env var:

```bash
docker run -p 8080:8080 \
  -e OPSTREAM__TRANSPORTS="signalr,websockets,grpc" \
  opstreamcollab/opstream:latest
```

---

## Scaling out — the full picture

When one node isn't enough, you add the **Redis backplane**. The backplane
does two things: fans operations out to peers connected to other nodes, and
coordinates *ownership* of each document so exactly one node is authoritative
at a time.

This diagram is the canonical "everything turned on" deployment: three
servers, every transport active, six client technologies, all editing the
same document, all kept in sync through Redis.

```mermaid
graph TD
    subgraph CLIENTS["✏️ Clients — all editing doc-42"]
        C1["🌐 HTML Client"]
        C2["⚡ Angular"]
        C3["⚛️ React"]
        C4["🔷 .NET Core"]
        C5["🐍 Python"]
        C6["📱 Swift / iOS"]
    end

    subgraph SRV1["🖥️ OpStream Server 1"]
        S1_SR["SignalR · /collab"]
        S1_WS["WebSocket · /collab-ws"]
        S1_GR["gRPC · :8080"]
    end

    subgraph SRV2["🖥️ OpStream Server 2"]
        S2_SR["SignalR · /collab"]
        S2_WS["WebSocket · /collab-ws"]
        S2_GR["gRPC · :8080"]
    end

    subgraph SRV3["🖥️ OpStream Server 3"]
        S3_SR["SignalR · /collab"]
        S3_WS["WebSocket · /collab-ws"]
        S3_GR["gRPC · :8080"]
    end

    REDIS[("🔴 Redis Backplane
    pub/sub · ownership map
    📄 doc-42 always in sync")]

    C1 -- WebSocket --> S1_WS
    C2 -- SignalR --> S1_SR
    C3 -- SignalR --> S2_SR
    C4 -- gRPC --> S2_GR
    C5 -- gRPC --> S3_GR
    C6 -- WebSocket --> S3_WS

    SRV1 <-->|fan-out ops| REDIS
    SRV2 <-->|fan-out ops| REDIS
    SRV3 <-->|fan-out ops| REDIS
```

What this diagram is telling you:

- **Heterogeneous clients are first-class.** Six different stacks, three
  different wire protocols, one document. No "preferred client". No bridge
  service in between.
- **One server handles every transport simultaneously.** Each `OpStream
  Server` box is a single `dotnet` process exposing SignalR + WebSocket +
  gRPC at the same time.
- **The backplane is the only stateful piece.** Servers are stateless;
  Redis owns the cross-node coordination. Add or remove a node and the
  cluster reconfigures itself.
- **Eventual convergence is automatic.** Whether an edit comes in over
  gRPC on node 3 or WebSocket on node 1, every connected peer ends up on
  the same revision.

---

## The server is .NET — the clients are not

This is the property that opens up real adoption: **the OpStream server runs
on .NET 9, but nothing about its wire protocol requires the client to.**

| Transport | Client requirement | Real client examples |
|---|---|---|
| **SignalR** | A SignalR client library (official or community) | JS/TS, .NET, Java, Python, Swift, C++ |
| **WebSockets** | A standards-compliant WebSocket implementation — i.e. **everything** | Browser native `WebSocket`, `ws` (Node), `websockets` (Python), `tokio-tungstenite` (Rust), Android `OkHttp`, Swift `URLSessionWebSocketTask` |
| **gRPC** | A gRPC client generated from the `.proto` | Any of the 11 official gRPC languages: Go, Rust, Java, C++, Node, Python, Ruby, PHP, Dart, … |

The op format on the wire is plain JSON (SignalR / WebSocket) or Protobuf
(gRPC). There is no `.NET`-shaped serialization, no `BinaryFormatter`, no
type tags that leak CLR information. **Anything that can open a socket and
parse JSON can be a first-class OpStream client.**

```mermaid
flowchart LR
    subgraph SERVER_SIDE["🟣 Server (.NET 9 — fixed)"]
        OPS["OpStream Server"]
    end

    subgraph CLIENT_SIDE["🟢 Clients (any language, any platform)"]
        direction TB
        JS["🌐 Browser JS / TS"]
        NG["⚡ Angular"]
        RC["⚛️ React"]
        VU["💚 Vue"]
        NET["🔷 .NET / MAUI / Blazor"]
        AND["🤖 Android · Kotlin"]
        IOS["📱 iOS · Swift"]
        PY["🐍 Python"]
        GO["🦫 Go"]
        RB["💎 Ruby"]
        RS["🦀 Rust"]
        BOT["🤖 LLM agent"]
    end

    CLIENT_SIDE <-->|JSON / Protobuf over WS / SignalR / gRPC| SERVER_SIDE
```

---

## Configuration matrix

Every layer is a choice you make at deploy time — and you can change your
mind without rewriting any application code.

| Dimension | Options | How you switch |
|---|---|---|
| **Transport** | SignalR · WebSocket · gRPC (any combination, simultaneously) | `OPSTREAM__TRANSPORTS="signalr,websockets,grpc"` |
| **Engines** | Text · Rich text · JSON · Tree · Table · Form (+ Awareness, Undo/Redo) | `OPSTREAM__ENGINES="text,json,tree"` |
| **Storage** | Memory · SQLite · PostgreSQL · MySQL · SQL Server · MongoDB · Redis | `OPSTREAM__STORAGE__PROVIDER=postgres` |
| **Backplane** | Local (single node) · Redis (cluster) | `OPSTREAM__BACKPLANE__PROVIDER=redis` |
| **Auth** | Anything implementing `IDocumentAuthorizer` — wraps your existing identity | `services.UseAuthorization<MyAuthorizer>()` |
| **TLS** | Terminate at the edge (Traefik, NGINX, Caddy, ALB, …) | Reverse proxy in front |

The same Docker image supports **every cell** of that matrix. A development
team can start with `memory` + `local` + `signalr` and graduate to `postgres`
+ `redis` + all three transports without changing a single line of code on
either side of the wire.

!!! example "From prototype to production — same code, different flags"
    **Day 1 — prototype**

    ```bash
    docker run -p 8080:8080 opstreamcollab/opstream:latest
    ```

    **Day 30 — production cluster**

    ```yaml
    OPSTREAM__TRANSPORTS: "signalr,websockets,grpc"
    OPSTREAM__ENGINES:    "text,json,rich-text,tree"
    OPSTREAM__STORAGE__PROVIDER: "postgres"
    OPSTREAM__STORAGE__CONNECTIONSTRING: "Host=..."
    OPSTREAM__BACKPLANE__PROVIDER: "redis"
    OPSTREAM__BACKPLANE__CONNECTIONSTRING: "redis:6379"
    ```

    No application redeploy. No client migration. No data conversion script.

---

## Data flow at runtime

To close the loop, here's what actually happens when two clients on different
servers edit the same document at the same time:

```mermaid
sequenceDiagram
    autonumber
    actor CA as 🌐 HTML Client A
    participant N1 as 🖥️ Node 1 (WS)
    participant R as 🔴 Redis backplane
    participant N2 as 🖥️ Node 2 (SignalR)
    actor CB as ⚡ Angular Client B

    Note over CA,CB: Both join doc-42

    CA->>N1: op · insert(0, "Hello ")
    N1->>N1: Engine.Apply → rev 1
    N1->>R: publish(doc-42, op, rev=1)
    R->>N2: deliver(op, rev=1)
    N2->>CB: op · insert(0, "Hello ")

    CB->>N2: op · insert(6, "world")
    N2->>N2: Engine.Transform + Apply → rev 2
    N2->>R: publish(doc-42, op, rev=2)
    R->>N1: deliver(op, rev=2)
    N1->>CA: op · insert(6, "world")

    Note over CA,CB: 📄 doc-42 = "Hello world" — converged
```

Three things to notice:

1. The client doesn't know or care which node owns the document.
2. The transport doesn't know or care what the document's shape is.
3. The engine doesn't know or care how the op arrived on the server.

That separation is what makes every other promise on this page possible.

---

## Where to go next

<div class="grid cards" markdown>

- :material-rocket-launch: **[5-minute quickstart](getting-started/quickstart.md)**
  Get the simple architecture running locally.

- :material-docker: **[Docker image](operations/docker.md)**
  Every configuration shown above as a ready-to-run env-var recipe.

- :material-graph: **[Backplane](operations/backplane.md)**
  Deep dive into the Redis fan-out and ownership protocol.

- :material-book-open-page-variant: **[Engines](engines/index.md)**
  Pick the right OT / CRDT engine for your document shape.

</div>
