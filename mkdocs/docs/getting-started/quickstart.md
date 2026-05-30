# Quickstart

Get two browser tabs editing the same document in five minutes — no .NET SDK
required.

## 1. Run the server

There's no project to create. Run the prebuilt image:

```bash
docker run -p 8080:8080 opstreamcollab/opstream
```

That starts OpStream with in-memory storage and the text / JSON / rich-text /
form engines enabled, listening on:

- **WebSockets** — `ws://localhost:8080/collab-ws`
- **SignalR** — `http://localhost:8080/collab`

> Prefer to embed the server inside your own ASP.NET Core app instead of the
> Docker image? See [Self-hosting the server](#self-hosting-the-server) below.

## 2. Connect a client

=== "HTML + JavaScript"

    Grab `WebSocketOpStreamClient.js` from the
    [samples](https://github.com/OpStreamCollab/OpStream/tree/main/samples) and
    drop this `index.html` next to it:

    ```html
    <textarea id="editor" style="width:100%;height:200px"></textarea>

    <script type="module">
      import { WebSocketOpStreamClient } from "./WebSocketOpStreamClient.js";

      const client = new WebSocketOpStreamClient("ws://localhost:8080/collab-ws");

      // Join the document — you get its current revision and a snapshot.
      const join = await client.connectAndJoinAsync("doc-1", "text");
      console.log("joined at revision", join.revision);

      // Remote edits from other tabs/users land here.
      client.onReceiveOp = (payload, newRevision) => {
        // `payload` is a base64-encoded TextOp — decode it and apply it to the
        // textarea. See the Text OT page for the op shape.
      };

      // Live presence: cursors, "who's here", "is typing".
      client.onReceiveAwareness = (peers) => {
        console.log("live peers:", peers.length);
      };

      // Send a local edit. Turning a textarea change into an op is a few lines;
      // the samples ship a ready-made textarea adapter that does it for you.
      // await client.sendOpAsync("doc-1", base64Op, join.revision);
    </script>
    ```

    Open the page in **two browser tabs** → both are in document `doc-1`. The
    complete, runnable version — textarea wired both ways, with live remote
    cursors — is the
    [HTML + JS sample](https://github.com/OpStreamCollab/OpStream/tree/main/samples).

    > No .NET anywhere on the client — just a WebSocket and a few lines of JS.
    > Any language that speaks the [wire protocol](../reference/wire-protocol.md)
    > connects the same way (Swift, Kotlin, Go, Python…).

=== ".NET / Blazor"

    ```bash
    dotnet add package OpStream.Client.Transports.SignalR
    ```

    ```csharp
    using OpStream.Client.Transports.SignalR;

    var client = new SignalROpStreamClient("http://localhost:8080/collab");
    await client.ConnectAsync();

    var join = await client.JoinAsync(documentId: "doc-1", documentType: "text");
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

    // Send a local op — see the Text OT page for the op shape.
    await client.SendOpAsync(opBytes, baseRevision: join.Revision);
    ```

## 3. What just happened?

1. Your client called **join**. The server routed it to the document's owner
   node (single-node for now), which loaded the document state from storage and
   sent back the current revision and a snapshot.
2. Your client sent an **op**. The owner node ran it through the engine for that
   document type: validated it, rebased it against any concurrent ops, applied
   it, persisted it, then broadcast the rebased op to every other connected peer.
3. **Awareness** pings (cursor positions, "user typing", …) flowed through the
   same connection but are **not persisted** — they're ephemeral state.

## Next steps

- **[What can you build](../recipes/collaborative-text-editor.md)** — full
  recipes for a text editor, a Notion-style doc, a spreadsheet, and a form.
- **[Choose your engine](../engines/index.md)** — pick the right algorithm for
  the document shape you actually have.
- **[Core concepts](concepts.md)** — documents, ops, revisions, and peers.

---

## Self-hosting the server

Prefer to embed OpStream in your own ASP.NET Core app rather than run the Docker
image? Create a project and wire it up:

```bash
dotnet new web -n CollabHello
cd CollabHello
dotnet add package OpStream.Server
dotnet add package OpStream.Server.Transports.SignalR
dotnet add package OpStream.Server.Storage.SqlServer
```

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

1. Registers OpStream's default services: the `TextOtEngine`, the in-memory
   store (about to be replaced), the local backplane, the open authorizer, and
   the snapshot pipeline.
2. Swaps in SQL Server storage. See [Storage backends](../storage/index.md) for
   the other options.
3. Adds the SignalR transport. Your clients connect over SignalR.
4. Maps the OpStream hub at the default route.
5. Initializes the router. Logs storage / backplane / engine choices and warns
   when defaults that aren't production-safe are still active.

Before shipping, replace the **two** defaults the framework warns about at
startup:

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
