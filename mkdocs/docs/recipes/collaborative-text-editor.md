# Recipe: Collaborative text editor

End-to-end walkthrough: an ASP.NET Core server plus a minimal Blazor
WASM client that lets two browsers edit the same document concurrently
with live cursors and undo.

## What we're building

- Plain-text document (`TextOtEngine`).
- SignalR transport.
- SQL Server storage.
- Awareness: live cursors per peer.
- Per-peer undo / redo.

## Server

```csharp
// Program.cs
using Microsoft.Extensions.DependencyInjection;
using OpStream.Server.Engine;
using OpStream.Server.Engine.Text;
using OpStream.Server.Engine.UndoRedo;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOpStream()
    .UseSqlServer(builder.Configuration.GetConnectionString("OpStream")!)
    .UseAuthorization<MyAuthorizer>()
    .AddSignalRTransport();

builder.Services.AddSignalR();

// Per-document UndoRedoEngine — keyed on the engine type that backs
// the "text" document type.
builder.Services.AddSingleton<UndoRedoEngine<TextDocument, TextOp>>(sp =>
    new UndoRedoEngine<TextDocument, TextOp>(
        sp.GetRequiredService<IOpEngine<TextDocument, TextOp>>()));

var app = builder.Build();
app.MapOpStreamSignalR();
app.Run();
```

## Client (Blazor)

```razor
@inject SignalROpStreamClient Client

<textarea @bind="text" @bind:event="oninput"
          @onkeyup="OnLocalChange"
          style="width:100%;height:60vh;font:14px monospace;" />

<button @onclick="UndoAsync">Undo</button>
<button @onclick="RedoAsync">Redo</button>

<div class="presence">
    @foreach (var peer in livePeers)
    {
        <span style="color:@peer.Color">●&nbsp;@peer.Name</span>
    }
</div>

@code {
    private string text = "";
    private long baseRevision;
    private List<PresenceModel> livePeers = new();

    protected override async Task OnInitializedAsync()
    {
        await Client.ConnectAsync();
        var join = await Client.JoinAsync("doc-1", "text");
        baseRevision = join.Revision;
        text = JsonSerializer.Deserialize<TextDocument>(join.Snapshot)!.Content;

        Client.OnReceiveOp += async (docId, payload, newRev) =>
        {
            var op = JsonSerializer.Deserialize<TextOp>(payload)!;
            text = ApplyLocally(text, op);
            baseRevision = newRev;
            await InvokeAsync(StateHasChanged);
        };

        Client.OnReceiveAwareness += peers =>
        {
            livePeers = peers.Select(MapPresence).ToList();
            InvokeAsync(StateHasChanged);
            return Task.CompletedTask;
        };
    }

    private async Task OnLocalChange()
    {
        // Diff against the previously sent text — see TextOp builder helpers.
        var op = BuildDiff(previousText, text);
        var result = await Client.SendOpAsync("doc-1",
            JsonSerializer.SerializeToUtf8Bytes(op),
            baseRevision);
        if (result.Success) baseRevision = result.NewRevision;
        previousText = text;
    }

    private Task UndoAsync() => InvokeUndoRedo(server => server.PrepareUndo);
    private Task RedoAsync() => InvokeUndoRedo(server => server.PrepareRedo);
}
```

## Undo / Redo wiring

`UndoRedoEngine` is currently standalone, so you call it from your hub
adapter or a small endpoint:

```csharp
app.MapPost("/opstream/undo/{docId}", async (string docId, HttpContext ctx,
    DocumentRouter router,
    UndoRedoEngine<TextDocument, TextOp> ur) =>
{
    var peerId = ctx.User.Identity!.Name!;
    var diag   = await router.GetDiagnosticsSnapshotAsync(docId);
    // Load the current state for currentState — see the SignalR client which
    // already maintains a typed local copy; alternatively replay from store.
    var currentState = await LoadStateAsync(docId);

    var prepared = ur.PrepareUndo(peerId, currentState);
    if (!prepared.HasOp) return Results.NoContent();

    var payload = JsonSerializer.SerializeToUtf8Bytes(prepared.Op!);
    var result  = await router.ApplyOpAsync(peerId, docId, payload, diag.Revision);
    if (result.Success) ur.NotifyUndoApplied(peerId, prepared.ConsumedSequence);

    return Results.Ok(result.Value);
});
```

(A first-class `EnableUndoRedo()` helper that wires this automatically
is on the v1.0 roadmap.)

## Cursors

Send your cursor on every selection change:

```csharp
await Client.SendAwarenessAsync("doc-1",
    JsonSerializer.SerializeToElement(new
    {
        cursor = new { line, col },
        selection = new[] { startLine, startCol, endLine, endCol },
        name = currentUserName,
        color = currentUserColor,
    }));
```

The fellow peers receive `OnReceiveAwareness` with the snapshot and
deltas; render them as small overlays anchored to the line / col.

## Try it

1. Run two browser tabs against the same `doc-1`.
2. Type in one; the other receives.
3. Hit Undo in one tab; it only undoes that tab's own changes — the
   other tab's edits remain.
4. Disconnect one tab; the other sees the cursor disappear within ~30 s.
