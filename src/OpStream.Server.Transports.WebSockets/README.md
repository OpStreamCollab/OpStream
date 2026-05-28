# OpStream.Server.Transports.WebSockets

Raw WebSocket server transport for OpStream. Exposes the OpStream session engine as an ASP.NET Core WebSocket middleware with no additional protocol overhead.

Best for scenarios requiring maximum control over the wire format.

## Installation

```bash
dotnet add package OpStream.Server.Transports.WebSockets --version 1.0.0
```

## Usage

1. Register the transport in your `IServiceCollection`:

```csharp
builder.Services.AddOpStream()
    .AddWebSocketTransport();
```

2. Map the endpoint in your `WebApplication`:

```csharp
app.MapOpStreamWebSockets("/collab-ws");
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
