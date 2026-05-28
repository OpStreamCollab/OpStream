# Engines overview

An **engine** is a pure, side-effect-free implementation of
`IOpEngine<TDoc, TOp>` that defines how a document type merges concurrent
edits. OpStream ships **eight** of them — six for persisted document
shapes and two transversal engines that work on top of the others.

## Choose by document shape

| Your document is… | Use | Family | Notes |
|---|---|---|---|
| Plain text | [Text OT](text-ot.md) | OT | Caret-aware; pairs with editors like Monaco, CodeMirror, contenteditable. |
| Rich text with attributes (bold, italic, lists…) | [Rich Text](rich-text.md) | OT | Quill / TipTap / ProseMirror style delta ops. |
| Free-form JSON object | [JSON CRDT](json-crdt.md) | CRDT | LWW per path. Best when keys are stable; no array splicing. |
| Hierarchical tree (outline, blocks, file system) | [Tree CRDT](tree-crdt.md) | CRDT | Native `Move` operation; Kleppmann's move-tree algorithm. |
| Spreadsheet / grid / Airtable-style | [Table CRDT](table-crdt.md) | CRDT | Rows + columns + cells with soft tombstones. |
| Bound form / settings dialog | [Form OT](form-ot.md) | LWW | Flatter and lighter than JSON CRDT. |

## Cross-cutting engines

| Engine | What it does |
|---|---|
| [Awareness](awareness.md) | Presence / cursors / "user is typing". Ephemeral — never persisted. |
| [Undo / Redo](undo-redo.md) | Per-peer undo / redo stacks layered on **any** of the persisted engines. |

## OT vs CRDT — which family do I want?

**Operational Transformation** (Text, Rich Text):

- :material-check: Smaller wire format for positional edits.
- :material-check: Server-authoritative, fits the "one master, many clients" model.
- :material-close: Requires a round-trip through the server to converge — no P2P.
- :material-close: Engine complexity is concentrated in `Transform`.

**Conflict-free Replicated Data Types** (JSON, Tree, Table, Form):

- :material-check: Operations commute by construction; `Transform` is identity.
- :material-check: P2P-friendly — peers can merge directly without a server.
- :material-check: Late-arriving updates don't break convergence.
- :material-close: Larger ops (carry timestamps + peer ids for LWW).
- :material-close: Some operations are not "natively atomic" (e.g. Move in a tree CRDT).

OpStream's CRDT engines all use `Timestamp + PeerId` LWW with deterministic
tie-breaking. The Tree engine uses Kleppmann's move-log algorithm and
absorbs late-arriving moves correctly.

## Typed vs untyped engines

Every engine has a runtime core that operates on `JsonElement` payloads.
Five of them also ship a generic strongly-typed wrapper:

| Untyped core | Typed wrapper |
|---|---|
| `AwarenessEngine` | (via `TypedAwarenessSession<TPresence>`) |
| `TreeCrdtEngine` | `TreeCrdtEngine<TPayload>` |
| `TableCrdtEngine` | `TableCrdtEngine<TValue>` |
| `FormOtEngine` | `FormOtEngine<TForm>` |

The typed wrappers serialize / deserialize at the engine boundary so your
application code works with domain POCOs. The wire format and storage
remain uniform across all clients.

## Registering an engine

Every engine plugs in through the builder:

```csharp
services.AddOpStream()
    .AddEngine<TreeDocument, TreeOpBatch, TreeCrdtEngine>("blocks");
```

The string is the **document type discriminator** the client sends when
joining — see [Builder API](../reference/builder-api.md#addengine).

## Next: pick an engine

<div class="grid cards" markdown>

- :material-text: **[Text OT](text-ot.md)** — collaborative plain text
- :material-format-bold: **[Rich Text](rich-text.md)** — Quill / ProseMirror
- :material-code-json: **[JSON CRDT](json-crdt.md)** — settings / config trees
- :material-file-tree: **[Tree CRDT](tree-crdt.md)** — Notion blocks / outliners
- :material-table: **[Table CRDT](table-crdt.md)** — spreadsheets / grids
- :material-form-textbox: **[Form OT](form-ot.md)** — forms / dialogs
- :material-account-eye: **[Awareness](awareness.md)** — cursors / presence
- :material-undo-variant: **[Undo / Redo](undo-redo.md)** — per-peer history

</div>
