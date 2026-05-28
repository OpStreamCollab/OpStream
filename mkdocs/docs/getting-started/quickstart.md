# Quickstart

Build a collaborative plain-text document in five minutes.

## 1. Create the project

```bash
dotnet new web -n CollabHello
cd CollabHello
dotnet add package OpStream.Server
dotnet add package OpStream.Server.Transports.SignalR
dotnet add package OpStream.Server.Storage.SqlServer
```

## 2. Configure services

Edit `Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOpStream()                                // (1)
    .UseSqlServer(builder.Configuration           // (2)
        .GetConnectionString("OpStream")!)
    .AddSignalRTransport();                       // (3)

builder.Services.AddSignalR();

var app = builder.Build();

app.MapOpStreamSignalR();                         // (4)
app.MapGet("/", () => "OpStream is running.");

await app.Services
    .GetRequiredService<DocumentRouter>()
    .InitializeAsync();                            // (5)

app.Run();
```

1. Registers OpStream's default services: the `TextOtEngine`, the
   in-memory store (about to be replaced), the local backplane, the
   open authorizer, and the snapshot pipeline.
2. Swaps in SQL Server storage. See [Storage backends](../storage/index.md)
   for the other options.
3. Adds the SignalR transport. Your clients will connect over SignalR.
4. Maps the OpStream hub at the default route (`/opstream/signalr`).
5. Initializes the router. Logs storage / backplane / engine choices and
   warns when defaults that aren't production-safe are still active.

## 3. Connect a client

From a Blazor / WPF / console app:

```csharp
using OpStream.Client.Transports.SignalR;

var client = new SignalROpStreamClient("https://localhost:5001/opstream/signalr");

await client.ConnectAsync();
var join = await client.JoinAsync(
    documentId: "doc-42",
    documentType: "text");

Console.WriteLine($"Joined at revision {join.Revision}");

client.OnReceiveOp += op =>
{
    // remote op arrived — re-render
    return Task.CompletedTask;
};

client.OnReceiveAwareness += peers =>
{
    Console.WriteLine($"Live peers: {peers.Count()}");
    return Task.CompletedTask;
};

// Send a local op — see the TextOtEngine page for the op shape.
await client.SendOpAsync(opBytes, baseRevision: join.Revision);
```

## 4. (Recommended) Replace the defaults

Before shipping to production, replace **two** defaults the framework warns
about at startup:

```csharp
builder.Services
    .AddOpStream()
    .UseSqlServer(connStr)
    .UseRedisBackplane(redisConnStr)              // multi-node fan-out
    .UseAuthorization<MyDocAuthorizer>()          // gate by user / role
    .AddSignalRTransport();
```

See [Authorization](../operations/authorization.md) and
[Backplane (scaling out)](../operations/backplane.md).

## 5. What just happened?

1. Your client called `JoinAsync`. The transport routed it to the
   document's **owner node** (single-node for now), which loaded the
   document state from storage.
2. Your client sent an op. The owner node ran it through the
   `TextOtEngine`: validated it, rebased it against any concurrent ops,
   applied it, persisted it, then broadcast the rebased op to every
   other connected peer.
3. Awareness pings (cursor positions, "user typing", …) flowed through
   the same hub but are **not persisted** — they're ephemeral state.

## Next steps

- **[Core concepts](concepts.md)** — understand documents, ops, revisions,
  and peers before you build for real.
- **[Engines overview](../engines/index.md)** — pick the right algorithm
  for the document shape you actually have.
- **[Authorization](../operations/authorization.md)** — wire OpStream into
  your existing identity model.
