# OpStream.Server

Core OpStream server engine. Provides OT and CRDT convergence algorithms (TextOT, RichText, JSON CRDT, List CRDT, Table, Tree, Form), document session management, consistent-hash document routing, awareness/presence, undo/redo, and snapshot policies.

All subsystems are interface-driven and DI-friendly.

## Installation

```bash
dotnet add package OpStream.Server --version 1.0.0
```

## Usage

Register OpStream services in your `Program.cs`:

```csharp
builder.Services.AddOpStream(options => {
    options.History.Enabled = true;
});
```

By default, OpStream uses in-memory storage and a local backplane. For production, consider using one of the available storage (Redis, SQL Server, etc.) and backplane (Redis) providers.

## License

This project is licensed under the [MIT License](https://github.com/OpStreamCollab/OpStream/blob/main/LICENSE).

## Links

- **Repository:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
- **Project Site:** [https://github.com/OpStreamCollab/OpStream](https://github.com/OpStreamCollab/OpStream)
