# OpStream.Client.Transports.SignalR

SignalR client transport for OpStream. Implements `IOpStreamClient` over ASP.NET Core SignalR for browser-friendly, fallback-capable real-time collaboration.

Requires `OpStream.Server.Transports.SignalR` on the server side.

## Installation

```bash
dotnet add package OpStream.Client.Transports.SignalR --version 1.0.0
```

## Usage

Configure the client to use SignalR in your `IServiceCollection`:

```csharp
builder.Services.AddOpStreamClient()
    .UseSignalRTransport(options => {
        options.HubUrl = "https://your-server/collab";
    });
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
