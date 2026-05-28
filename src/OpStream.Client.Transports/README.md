# OpStream.Client.Transports

Transport abstraction layer for OpStream clients. Provides the `IOpStreamClient` interface for connecting to OpStream servers, submitting operations, and receiving real-time updates.

Pick a concrete transport package (SignalR, gRPC, WebSockets) to complement this one.

## Installation

```bash
dotnet add package OpStream.Client.Transports --version 1.0.0
```

## Usage

Register the client builder in your `IServiceCollection`:

```csharp
builder.Services.AddOpStreamClient();
```

Then, chain one of the concrete transport implementations:

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
