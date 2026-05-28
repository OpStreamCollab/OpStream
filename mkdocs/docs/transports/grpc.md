# gRPC transport

Streaming gRPC transport for polyglot service-to-service or strongly-typed
clients (Go, Rust, Kotlin, Swift). Built on `Grpc.AspNetCore`.

## Install

```bash
dotnet add package OpStream.Server.Transports.gRPC
```

## Server setup

```csharp
builder.Services
    .AddOpStream()
    .UseSqlServer(connStr)
    .AddGrpcTransport();

builder.Services.AddGrpc();

var app = builder.Build();
app.MapOpStreamGrpc();        // Maps OpStreamService at its default path
```

The service definition lives in `Protos/opstream.proto`. Copy it into
your client project to generate a typed client in any language gRPC
supports.

## Service surface

```protobuf
service OpStreamService {
  rpc JoinDocument(JoinRequest) returns (stream ServerEvent);
  rpc SendOp(OpRequest) returns (OpResponse);
  rpc UpdateAwareness(AwarenessRequest) returns (AwarenessAck);
}
```

`JoinDocument` is a **server-streaming** RPC — the client subscribes once,
the server pushes `ServerEvent`s (ops, awareness, peer disconnects) for
the lifetime of the connection.

## .NET client

```bash
dotnet add package OpStream.Client.Transports.gRPC
```

```csharp
using OpStream.Client.Transports.gRPC;

var client = new gRPCOpStreamClient("https://your-app");
await client.ConnectAsync();

var join = await client.JoinAsync("doc-1", "text");
client.OnReceiveOp += ... ;
await client.SendOpAsync("doc-1", opBytes, join.Revision);
```

## Authentication

Pass your auth token via the `Authorization` metadata header. The
standard gRPC `CallCredentials` pattern works:

```csharp
var channel = GrpcChannel.ForAddress("https://your-app",
    new GrpcChannelOptions
    {
        Credentials = ChannelCredentials.Create(
            new SslCredentials(),
            CallCredentials.FromInterceptor(async (ctx, metadata) =>
            {
                metadata.Add("Authorization", $"Bearer {await GetTokenAsync()}");
            }))
    });
```

Server-side, the ASP.NET Core authentication pipeline produces a
`ClaimsPrincipal` that your `IDocumentAuthorizer` can read.

## Scaling out

Combine with the [Redis backplane](../operations/backplane.md) for
multi-node ownership coordination. gRPC's HTTP/2 multiplexing means a
single client connection can carry many concurrent streams without
needing connection affinity.

## When to pick gRPC

- :material-check: Polyglot clients (Go / Rust / Swift / Kotlin / Python).
- :material-check: Service-to-service collaboration in a mesh.
- :material-check: You already invest in `.proto` schemas and tooling.
- :material-close: Browser clients — gRPC-Web works but adds proxy complexity.
  SignalR or WebSockets are simpler.
