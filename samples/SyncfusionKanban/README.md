# Collaborative Kanban board (Syncfusion Blazor + OpStream)

A Trello-style board where the whole team moves cards at once — add a card in one
browser tab, drag it between columns, edit or delete it, and everyone sees the
change in real time. Built with the
[Syncfusion Blazor Kanban](https://blazor.syncfusion.com/demos/kanban/overview)
component; collaboration is added over OpStream's **JSON CRDT** engine.

## How it works

- **Engine**: `json` (JSON CRDT). Each card is stored as a register at
  `cards.<id>` = `{ id, status, title, summary, assignee, priority, order }`.
- **Capture**: `ActionBegin` pre-assigns a GUID `Id` to new cards.
  `ActionComplete` fires after every CRUD action — `AddedRecords`,
  `ChangedRecords`, and `DeletedRecords` map to `set` / `del` ops that are
  sent to OpStream.
- **Apply**: `ReceiveOp` parses the incoming op and calls `AddCardAsync`,
  `UpdateCardAsync`, or `DeleteCardAsync` on the `SfKanban` reference.
- **Guard**: `_remoteApplyDepth` counter prevents re-broadcasting ops that were
  applied by a remote peer.
- **Presence**: each peer broadcasts its name and color; avatars appear in the
  status bar.

## Project structure

```
SyncfusionKanban.View/   ← Razor Class Library (the reusable component)
  KanbanCard.cs          ← data model
  SyncfusionKanbanDemo.razor
SyncfusionKanban/        ← standalone host for local development
  Program.cs
  Components/...
```

## Run it locally

```bash
# 1. OpStream server (Docker)
docker run -p 50109:8080 opstreamcollab/opstream

# 2. The standalone host
cd samples/SyncfusionKanban
dotnet run
```

Open <http://localhost:5000> in two or more tabs. Add cards via the **+** button
in any column, drag them between columns, double-click to edit, or click the
delete icon — every action propagates to all open tabs.

> The `appsettings.Development.json` already points `/collab` at
> `http://localhost:50109/collab`. Change the port if your OpStream server
> listens elsewhere.

## Syncfusion license

Without a license key, Syncfusion renders a small **Trial** watermark but the
component is fully functional. To register a key, uncomment the line in
`Program.cs`:

```csharp
Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense("YOUR-KEY");
```

Community licenses are free for individuals and companies with < $1 M annual
revenue: <https://www.syncfusion.com/products/communitylicense>

## Known limitations / points to verify

- **`ActionComplete` for drag-drop**: the moved card should appear in
  `ChangedRecords` with its new `Status` field. Confirmed in Syncfusion docs but
  worth verifying the exact `RequestType` / record population at runtime.
- **`UpdateCardAsync(card, idx)`**: the `idx` parameter is the 0-based position
  in the DataSource list. If Syncfusion ignores it and locates the card by value
  matching, the index is irrelevant; verify no visual glitch on remote updates.
- **Dialog `Id` field**: the built-in add/edit dialog shows all model fields.
  `Id` and `Order` are hidden via CSS (`.e-kanban-dialog [id$="Id"]`). If the
  selector doesn't match your Syncfusion version, the user will see these fields
  but can safely ignore them.
- **Echo suppression**: the OpStream server does not re-send an op back to its
  sender, so all `ReceiveOp` callbacks are guaranteed remote. The
  `_remoteApplyDepth` guard is a belt-and-suspenders defence in case Syncfusion's
  `AddCardAsync` / `UpdateCardAsync` / `DeleteCardAsync` internally fires
  `ActionComplete`.
