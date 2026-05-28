# SignalR transport

The default transport for browser / Blazor / .NET clients. Built on
ASP.NET Core SignalR, so reconnection, fallback, and authentication
integration are inherited from the host.

## Install

```bash
dotnet add package OpStream.Server.Transports.SignalR
```

## Server setup

```csharp
builder.Services
    .AddOpStream()
    .UseSqlServer(connStr)
    .AddSignalRTransport();          // (1)

builder.Services.AddSignalR();       // (2)

var app = builder.Build();
app.MapOpStreamSignalR();            // (3)
```

1. Register the OpStream SignalR hub services.
2. Standard ASP.NET Core SignalR bootstrap.
3. Map the hub at the default path (`/opstream/signalr`). Override:
   `app.MapOpStreamSignalR("/realtime/op")`.

## Hub methods (client → server)

| Method | Args | Returns |
|---|---|---|
| `JoinDocument` | `documentId`, `documentType`, `protocolVersion` | `SessionJoinResult` |
| `SendOp` | `documentId`, `payload (byte[])`, `baseRevision` | `OpApplyResult` |
| `UpdateAwareness` | `documentId`, `data (JsonElement)` | `void` |

Method names live in `OpStreamConstants.HubMethods`.

## Client events (server → client)

| Event | Payload |
|---|---|
| `ReceiveOp` | `(documentId, opBytes, newRevision)` |
| `ReceiveAwareness` | List of live `AwarenessState` (initial snapshot on join) |
| `ReceiveAwarenessUpdate` | A single `AwarenessState` delta |
| `PeerDisconnected` | `peerId` |

Event names live in `OpStreamConstants.ClientEvents`.

## .NET client

```bash
dotnet add package OpStream.Client.Transports.SignalR
```

```csharp
using OpStream.Client.Transports.SignalR;

var client = new SignalROpStreamClient("https://your-app/opstream/signalr");
await client.ConnectAsync();

var join = await client.JoinAsync("doc-1", "text");
Console.WriteLine($"Joined at rev {join.Revision}");

client.OnReceiveOp += (docId, opBytes, rev) => { /* apply locally */ return Task.CompletedTask; };
client.OnReceiveAwareness += peers => { /* render presence */ return Task.CompletedTask; };

await client.SendOpAsync(docId: "doc-1", opBytes, baseRevision: join.Revision);
await client.SendAwarenessAsync("doc-1", JsonSerializer.SerializeToElement(cursor));
```

## Authentication

The hub honors whatever ASP.NET Core authentication you've configured —
add `[Authorize]` to the OpStream hub via standard SignalR options:

```csharp
builder.Services.AddSignalR(o => { /* options */ });
builder.Services.AddAuthentication(/* ... */);
builder.Services.AddAuthorization();
```

The peer id assigned at connect time is `Context.UserIdentifier` when
available, otherwise the `ConnectionId`. Override the user-id resolver
via the standard SignalR `IUserIdProvider`.

For document-level authorization, plug an `IDocumentAuthorizer` via
[`UseAuthorization<T>()`](../operations/authorization.md).

## CORS and origins

If your client lives on a different origin, configure ASP.NET Core CORS
with `WithCredentials()` — required for the SignalR connection handshake.

## Scaling out

To run more than one server node, add the
[Redis backplane](../operations/backplane.md). SignalR's own Redis
backplane is **not** required — OpStream's backplane fans ops out at the
engine level, which is more efficient than SignalR group broadcasts.

## See also

- [Hub method constants](../reference/builder-api.md#opstreamconstants)
- [Wire protocol](../reference/wire-protocol.md)
