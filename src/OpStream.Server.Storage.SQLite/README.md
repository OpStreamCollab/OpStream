# OpStream.Server.Storage.SQLite

SQLite storage for OpStream via Entity Framework Core. Zero-configuration, file-based persistence for development environments, desktop apps, and single-server deployments.

Provides EF Core migrations out of the box.

## Installation

```bash
dotnet add package OpStream.Server.Storage.SQLite --version 1.0.0
```

## Usage

Configure OpStream to use SQLite for storage:

```csharp
builder.Services.AddOpStream()
    .UseSqliteStorage("Data Source=opstream.db");
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
