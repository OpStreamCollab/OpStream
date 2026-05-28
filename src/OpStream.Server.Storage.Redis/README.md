# OpStream.Server.Storage.Redis

Redis storage for OpStream. Stores document snapshots as Redis Strings and operation logs as Redis Streams, with automatic XTRIM-based compaction.

Best for high-throughput scenarios where sub-millisecond storage latency matters.

## Installation

```bash
dotnet add package OpStream.Server.Storage.Redis --version 1.0.0
```

## Usage

Configure OpStream to use Redis for storage:

```csharp
builder.Services.AddOpStream()
    .UseRedisStorage("your-redis-connection-string");
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
