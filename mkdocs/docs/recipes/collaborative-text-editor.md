# Recipe: Collaborative text editor

Two browsers editing the same document at once, with live cursors and
per-user undo. We'll show the client in **plain HTML + JavaScript** (the fast
path) and in **Blazor** — both talking to the same server.

## What you're building

- A plain-text document (the **Text** engine).
- Live cursors for every connected user.
- Per-user undo / redo.

> **You don't have to write the server to try this.** `docker run -p 8080:8080
> opstreamcollab/opstream` gives you one with the text engine ready, listening
> on `ws://localhost:8080/collab-ws`. Jump straight to [the client](#the-client).
> The server section below is for when you want your own — with your storage and
> your auth.

## Server (optional — bring your own)

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

## The client

### HTML + JavaScript

This is the fast path — no .NET on the client. The working sample wires a
Monaco editor to OpStream in a few lines with the `attachCollab` helper from
`monaco-collab.js`; it runs the merge (OT) state machine and renders remote
cursors for you:

```html
<div id="editor" style="height:60vh"></div>

<script type="module">
  import { attachCollab } from "./monaco-collab.js";

  const editor = monaco.editor.create(
    document.getElementById("editor"), { value: "", language: "markdown" });

  attachCollab(editor, {
    url: "ws://localhost:8080/collab-ws",
    documentId: "doc-1",
    presence: { name: "Ada", color: "#e91e63" },   // your live cursor
  });
</script>
```

That's the whole client. Open it in two tabs and they're editing `doc-1`
together, cursors and all. The full runnable version — Monaco set-up, the
`monaco-collab.js` / `WebSocketOpStreamClient.js` helpers, plus a plain
`<textarea>` variant — is in the
[HTML + JS samples](https://github.com/OpStreamCollab/OpStream/tree/main/samples).

Prefer another editor? The same helper pattern works for CodeMirror, a plain
`<textarea>`, or any `contenteditable` — only the editor-binding glue changes.

### Blazor

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
