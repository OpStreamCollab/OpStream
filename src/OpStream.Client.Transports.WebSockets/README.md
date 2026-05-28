# OpStream.Client.Transports.WebSockets

Raw WebSocket client transport for OpStream. Implements `IOpStreamClient` over a plain WebSocket connection for maximum portability with minimal dependencies.

Requires `OpStream.Server.Transports.WebSockets` on the server side.

## Installation

```bash
dotnet add package OpStream.Client.Transports.WebSockets --version 1.0.0
```

## Usage

Configure the client to use WebSockets in your `IServiceCollection`:

```csharp
builder.Services.AddOpStreamClient()
    .UseWebSocketTransport(options => {
        options.ServerUrl = "wss://your-server/collab-ws";
    });
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
