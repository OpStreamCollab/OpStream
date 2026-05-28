# OpStream.Server.Storage.PostgreSQL

PostgreSQL storage for OpStream via Entity Framework Core and Npgsql. Stores operation logs and document snapshots in PostgreSQL.

Provides EF Core migrations and takes advantage of PostgreSQL's JSONB and LISTEN/NOTIFY capabilities.

## Installation

```bash
dotnet add package OpStream.Server.Storage.PostgreSQL --version 1.0.0
```

## Usage

Configure OpStream to use PostgreSQL for storage:

```csharp
builder.Services.AddOpStream()
    .UsePostgreSqlStorage("your-postgresql-connection-string");
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
