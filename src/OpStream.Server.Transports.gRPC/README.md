# OpStream.Server.Transports.gRPC

gRPC server transport for OpStream. Exposes the OpStream session engine as a bidirectional gRPC streaming service using Grpc.AspNetCore.

Best for .NET-to-.NET scenarios requiring maximum throughput and binary framing.

## Installation

```bash
dotnet add package OpStream.Server.Transports.gRPC --version 1.0.0
```

## Usage

1. Register the transport in your `IServiceCollection`:

```csharp
builder.Services.AddOpStream()
    .AddGrpcTransport();
```

2. Map the gRPC service in your `WebApplication`:

```csharp
app.MapOpStreamGrpc();
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
