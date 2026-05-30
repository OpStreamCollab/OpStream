# Transports overview

A **transport** is simply *how your client connects* to the OpStream server.

> **Short answer:** building a web or Blazor app? Use **SignalR**. A mobile,
> native, or hand-rolled JavaScript client? **WebSockets**. Service-to-service
> or polyglot? **gRPC**. You can also enable several at once.

Three are first-class:

| Transport | Best for | Package |
|---|---|---|
| [SignalR](signalr.md) | Web / Blazor / .NET clients, easy reconnection, fallbacks | `OpStream.Server.Transports.SignalR` |
| [WebSockets](websockets.md) | Raw-WS clients (mobile, native, custom protocols) | `OpStream.Server.Transports.WebSockets` |
| [gRPC](grpc.md) | Service-to-service, polyglot clients, streaming | `OpStream.Server.Transports.gRPC` |

All three sit on top of the same `DocumentRouter` — they're just different
serializations of the same operations: **JoinDocument**, **SendOp**, and
**UpdateAwareness** in / **ReceiveOp**, **ReceiveAwareness**,
**PeerDisconnected** out.

## Picking one

- :material-web: **Browser or Blazor client** → SignalR is the path of
  least resistance: built-in reconnection, fallback to long-polling on
  hostile networks, dead-simple `services.AddSignalR()` integration.
- :material-cellphone: **Mobile / native client** → WebSockets keeps the
  protocol lean and avoids the SignalR client-side runtime.
- :material-server-network: **Service mesh / polyglot** → gRPC for
  bidirectional streaming with strong typing across languages.

You can register **more than one** transport in the same app:

```csharp
services.AddOpStream()
    .UseSqlServer(connStr)
    .AddSignalRTransport()       // /opstream/signalr
    .AddWebSocketTransport()     // /opstream/ws
    .AddGrpcTransport();         // /OpStream.Server.Transports.gRPC.OpStreamService
```

Each transport exposes its own endpoint; the router and storage are shared.

## Endpoint mapping

Every transport's package also ships an `Map*` extension on
`IEndpointRouteBuilder`:

```csharp
app.MapOpStreamSignalR();
app.MapOpStreamWebSockets();
app.MapOpStreamGrpc();
```

Override the path with the optional argument; see each transport page.

## Wire compatibility across transports

The op payload **on the wire** is the engine-specific JSON — the same
shape produced by `JsonSerializer.SerializeToUtf8Bytes(op,
OpStreamJsonOptions.Default)`. A client that knows the engine can talk
to any of the three transports interchangeably.

See [Wire protocol](../reference/wire-protocol.md).
