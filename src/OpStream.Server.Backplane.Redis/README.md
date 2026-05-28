# OpStream.Server.Backplane.Redis

Redis pub/sub backplane for OpStream. Enables multi-node deployments by broadcasting operations and awareness events across server instances via Redis Pub/Sub.

Add this package when running OpStream behind a load balancer or in a Kubernetes cluster.

## Installation

```bash
dotnet add package OpStream.Server.Backplane.Redis --version 1.0.0
```

## Usage

Configure OpStream to use the Redis backplane:

```csharp
builder.Services.AddOpStream()
    .UseRedisBackplane("your-redis-connection-string");
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
