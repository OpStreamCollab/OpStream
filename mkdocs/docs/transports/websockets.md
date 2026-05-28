# WebSockets transport

Bare WebSocket transport for clients that don't want a SignalR runtime —
mobile apps, native desktop, or any custom client speaking a JSON
framing protocol.

## Install

```bash
dotnet add package OpStream.Server.Transports.WebSockets
```

## Server setup

```csharp
builder.Services
    .AddOpStream()
    .UseSqlServer(connStr)
    .AddWebSocketTransport();

var app = builder.Build();

app.UseWebSockets();              // standard ASP.NET Core middleware
app.MapOpStreamWebSockets();      // default path /opstream/ws
```

Override the path: `app.MapOpStreamWebSockets("/realtime");`

## Message framing

Each WebSocket message is a UTF-8 JSON object with a `type` discriminator
plus per-type fields. See `WebSocketMessages.cs`.

### Client → server

```json
// Join a document
{ "type": "join",        "documentId": "doc-1", "documentType": "text", "protocolVersion": 1 }

// Send an op
{ "type": "op",          "documentId": "doc-1", "payload": "<base64>", "baseRevision": 7 }

// Update awareness
{ "type": "awareness",   "documentId": "doc-1", "data": { ... } }
```

### Server → client

```json
// Join ack — the document's current state and revision
{ "type": "join_ack",         "revision": 7, "snapshot": "<base64>", "awareness": [ ... ] }

// Remote op landed
{ "type": "op",               "documentId": "doc-1", "payload": "<base64>", "revision": 8 }

// Peer awareness changed
{ "type": "awareness_update", "documentId": "doc-1", "peerId": "p-3", "data": { ... }, "lastUpdated": "..." }

// Peer disconnected
{ "type": "peer_disconnected", "peerId": "p-3" }
```

The discriminator strings live in `OpStreamConstants.WebSocketMessages`.

## .NET client

```bash
dotnet add package OpStream.Client.Transports.WebSockets
```

```csharp
using OpStream.Client.Transports.WebSockets;

var client = new WebSocketOpStreamClient("wss://your-app/opstream/ws");
await client.ConnectAsync();

var join = await client.JoinAsync("doc-1", "text");
client.OnReceiveOp += ... ;
await client.SendOpAsync(...);
```

The API surface mirrors the SignalR client; the only difference is the
URL scheme and the underlying transport.

## Authentication

WebSocket upgrades go through the ASP.NET Core authentication pipeline.
Pass your token via the standard `Authorization` header on the initial
HTTP request, or via a query string (`?access_token=...`) — whichever
your client supports.

For document-level access, plug `IDocumentAuthorizer` via
[`UseAuthorization<T>()`](../operations/authorization.md).

## Scaling out

Use the [Redis backplane](../operations/backplane.md). The WebSocket
transport fan-outs ops via the backplane to peers on other nodes; no
sticky sessions required as long as the load balancer distributes the
initial HTTP upgrade.

## Choosing WebSockets over SignalR

- :material-check: Smaller client footprint (no SignalR JS / .NET runtime).
- :material-check: Trivial to consume from a non-.NET client (Swift / Kotlin / Go).
- :material-close: No automatic reconnection — your client implements it.
- :material-close: No transport fallback (long-polling) — your network must support WS.
