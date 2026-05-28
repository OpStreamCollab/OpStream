# OpStream.Server.Storage.SqlServer

SQL Server storage for OpStream via Entity Framework Core. Stores operation logs and document snapshots in Microsoft SQL Server or Azure SQL.

Provides EF Core migrations and supports both on-premises and Azure SQL deployments.

## Installation

```bash
dotnet add package OpStream.Server.Storage.SqlServer --version 1.0.0
```

## Usage

Configure OpStream to use SQL Server for storage:

```csharp
builder.Services.AddOpStream()
    .UseSqlServerStorage("your-sqlserver-connection-string");
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
