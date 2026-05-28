# OpStream.Server.Storage.MongoDB

MongoDB document storage for OpStream. Stores operation logs and snapshots in MongoDB collections.

Ideal for document-heavy workloads or teams already running MongoDB. Implements `IDocumentStore` via the official MongoDB.Driver.

## Installation

```bash
dotnet add package OpStream.Server.Storage.MongoDB --version 1.0.0
```

## Usage

Configure OpStream to use MongoDB for storage:

```csharp
builder.Services.AddOpStream()
    .UseMongoDbStorage("your-mongodb-connection-string", "OpStreamDb");
```

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
