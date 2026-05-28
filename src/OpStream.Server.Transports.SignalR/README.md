# OpStream.Server.Transports.SignalR

SignalR server transport for OpStream. Exposes the OpStream session engine as an ASP.NET Core SignalR hub.

Supports WebSocket, Server-Sent Events, and Long-Polling fallbacks, making it compatible with browsers and Blazor applications.

## Installation

```bash
dotnet add package OpStream.Server.Transports.SignalR --version 1.0.0
```

## Usage

1. Register the transport in your `IServiceCollection`:

```csharp
builder.Services.AddOpStream()
    .AddSignalRTransport();
```

2. Map the hub in your `WebApplication`:

```csharp
app.MapOpStreamSignalR("/collab");
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
