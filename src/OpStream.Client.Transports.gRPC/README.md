# OpStream.Client.Transports.gRPC

gRPC client transport for OpStream. Implements `IOpStreamClient` over a bidirectional gRPC stream for low-latency, multiplexed real-time collaboration.

Requires `OpStream.Server.Transports.gRPC` on the server side.

## Installation

```bash
dotnet add package OpStream.Client.Transports.gRPC --version 1.0.0
```

## Usage

Configure the client to use gRPC in your `IServiceCollection`:

```csharp
builder.Services.AddOpStreamClient()
    .UsegRPCTransport(options => {
        options.BaseUrl = "https://your-server:5001";
    });
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
