# MongoDB storage

Document-database backend. Pairs naturally with applications that
already use MongoDB for their business data.

## Install

```bash
dotnet add package OpStream.Server.Storage.MongoDB
```

## Setup

```csharp
builder.Services
    .AddOpStream()
    .UseMongoDbStorage(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("Mongo")!;
        options.DatabaseName     = "opstream";
        // Optional collection name overrides
        options.OpsCollectionName       = "ops";
        options.SnapshotsCollectionName = "snapshots";
    });
```

## Collection shape

```js
// ops
{
  _id:        ObjectId,
  documentId: "...",
  revision:   42,
  authorId:   "peer-7",
  timestamp:  ISODate("..."),
  payload:    BinData,
  engineType: "TextOp"
}
// Compound index: { documentId: 1, revision: 1 }

// snapshots
{
  _id:        "documentId",   // _id is the document id
  revision:   42,
  timestamp:  ISODate("..."),
  state:      BinData
}
```

## Performance notes

- The unique compound index on `(documentId, revision)` is created on
  first use. It's also the natural sort key for `StreamOpsAsync`.
- For multi-region MongoDB Atlas clusters, pin OpStream's connection
  string to the **primary** read preference; reading from secondaries
  risks reading a stale op log between writes.

## When to pick this

- :material-check: Business data is already in MongoDB — operational
  expertise is reused.
- :material-check: Schemaless ergonomics for ad-hoc payload shapes.
- :material-close: You need ACID transactions across multiple documents
  in the same OpStream session — use a relational backend.
