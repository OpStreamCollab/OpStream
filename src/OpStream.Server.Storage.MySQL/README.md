# OpStream.Server.Storage.MySQL

MySQL storage for OpStream via Entity Framework Core and Pomelo.EntityFrameworkCore.MySql. Stores operation logs and document snapshots in a MySQL or MariaDB database.

Provides EF Core migrations out of the box.

## Installation

```bash
dotnet add package OpStream.Server.Storage.MySQL --version 1.0.0
```

## Usage

Configure OpStream to use MySQL for storage:

```csharp
builder.Services.AddOpStream()
    .UseMySqlStorage("your-mysql-connection-string");
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
